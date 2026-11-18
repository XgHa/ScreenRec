#define NOMINMAX
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <propvarutil.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "propsys.lib")

using Microsoft::WRL::ComPtr;

namespace
{
constexpr LONGLONG kTicksPerSecond = 10'000'000LL;

void CheckHr(HRESULT hr, const char* operation)
{
    if (SUCCEEDED(hr)) return;
    std::ostringstream message;
    message << operation << " failed, hr=0x" << std::hex << std::uppercase
            << static_cast<unsigned long>(hr);
    throw std::runtime_error(message.str());
}

struct ProbeInfo
{
    LONGLONG Duration = 0;
    bool HasVideo = false;
    bool HasAudio = false;
    UINT32 Width = 0;
    UINT32 Height = 0;
    ComPtr<IMFMediaType> VideoType;
    ComPtr<IMFMediaType> AudioType;
};

LONGLONG ReadDuration(const ComPtr<IMFSourceReader>& reader)
{
    PROPVARIANT value;
    PropVariantInit(&value);
    HRESULT hr = reader->GetPresentationAttribute(
        static_cast<DWORD>(MF_SOURCE_READER_MEDIASOURCE), MF_PD_DURATION, &value);
    LONGLONG duration = 0;
    if (SUCCEEDED(hr))
    {
        if (value.vt == VT_UI8) duration = static_cast<LONGLONG>(value.uhVal.QuadPart);
        else if (value.vt == VT_I8) duration = value.hVal.QuadPart;
    }
    PropVariantClear(&value);
    return duration;
}

void ReadVideoFormat(const ComPtr<IMFMediaType>& type, ProbeInfo& info)
{
    UINT32 width = 0, height = 0;
    if (SUCCEEDED(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &width, &height)))
    {
        info.Width = width;
        info.Height = height;
    }
}

ProbeInfo OpenInput(const std::wstring& path, ComPtr<IMFSourceReader>& reader)
{
    CheckHr(MFCreateSourceReaderFromURL(path.c_str(), nullptr, &reader), "MFCreateSourceReaderFromURL");
    CheckHr(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE), "disable all streams");

    ProbeInfo info;
    HRESULT videoHr = reader->GetNativeMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, &info.VideoType);
    if (SUCCEEDED(videoHr))
    {
        info.HasVideo = true;
        CheckHr(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), TRUE), "select video stream");
        CheckHr(reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, info.VideoType.Get()),
                "select compressed video format");
        ReadVideoFormat(info.VideoType, info);
    }

    HRESULT audioHr = reader->GetNativeMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), 0, &info.AudioType);
    if (SUCCEEDED(audioHr))
    {
        info.HasAudio = true;
        CheckHr(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), TRUE), "select audio stream");
        CheckHr(reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), nullptr, info.AudioType.Get()),
                "select compressed audio format");
    }

    if (!info.HasVideo && !info.HasAudio) throw std::runtime_error("input has no supported audio or video stream");
    info.Duration = ReadDuration(reader);
    return info;
}

UINT64 GetRatio(IMFMediaType* type, REFGUID key, UINT64 defaultValue)
{
    UINT64 value = 0;
    return SUCCEEDED(type->GetUINT64(key, &value)) ? value : defaultValue;
}

void Require(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
}

void RequireSameGuid(IMFMediaType* left, IMFMediaType* right, REFGUID key, const char* message)
{
    GUID a = GUID_NULL, b = GUID_NULL;
    CheckHr(left->GetGUID(key, &a), "read media type GUID");
    CheckHr(right->GetGUID(key, &b), "read media type GUID");
    Require(a == b, message);
}

void RequireSameBlob(IMFMediaType* left, IMFMediaType* right)
{
    UINT32 leftSize = 0, rightSize = 0;
    HRESULT leftHr = left->GetBlobSize(MF_MT_USER_DATA, &leftSize);
    HRESULT rightHr = right->GetBlobSize(MF_MT_USER_DATA, &rightSize);
    if (leftHr == MF_E_ATTRIBUTENOTFOUND && rightHr == MF_E_ATTRIBUTENOTFOUND) return;
    CheckHr(leftHr, "read left codec configuration size");
    CheckHr(rightHr, "read right codec configuration size");
    Require(leftSize == rightSize, "codec configuration changed between segments");
    if (leftSize == 0) return;

    std::vector<UINT8> a(leftSize), b(rightSize);
    CheckHr(left->GetBlob(MF_MT_USER_DATA, a.data(), leftSize, nullptr), "read left codec configuration");
    CheckHr(right->GetBlob(MF_MT_USER_DATA, b.data(), rightSize, nullptr), "read right codec configuration");
    Require(std::memcmp(a.data(), b.data(), leftSize) == 0, "codec configuration changed between segments");
}

void RequireCompatible(const ProbeInfo& reference, const ProbeInfo& next)
{
    Require(reference.HasVideo == next.HasVideo, "video stream presence changed between segments");
    Require(reference.HasAudio == next.HasAudio, "audio stream presence changed between segments");

    if (reference.HasVideo)
    {
        auto* a = reference.VideoType.Get();
        auto* b = next.VideoType.Get();
        RequireSameGuid(a, b, MF_MT_MAJOR_TYPE, "video major type changed");
        RequireSameGuid(a, b, MF_MT_SUBTYPE, "video codec changed between segments");
        Require(reference.Width == next.Width && reference.Height == next.Height,
                "video dimensions changed between segments");
        // MF_MT_FRAME_RATE is a *nominal* rate derived from sample timestamps, not an
        // encoder setting. Segments of the same recording disagree here as soon as frames
        // are dropped (measured: 60/1 vs 30/1) while their avcC SPS/PPS stay byte-identical.
        // Every sample keeps its own duration and we rewrite PTS/DTS, so the nominal value
        // never reaches the output - rejecting the merge on it only loses good footage.
        Require(GetRatio(a, MF_MT_PIXEL_ASPECT_RATIO, 0) == GetRatio(b, MF_MT_PIXEL_ASPECT_RATIO, 0),
                "video pixel aspect ratio changed between segments");
        RequireSameBlob(a, b);
    }

    if (reference.HasAudio)
    {
        auto* a = reference.AudioType.Get();
        auto* b = next.AudioType.Get();
        RequireSameGuid(a, b, MF_MT_MAJOR_TYPE, "audio major type changed");
        RequireSameGuid(a, b, MF_MT_SUBTYPE, "audio codec changed between segments");
        Require(MFGetAttributeUINT32(a, MF_MT_AUDIO_NUM_CHANNELS, 0) == MFGetAttributeUINT32(b, MF_MT_AUDIO_NUM_CHANNELS, 0),
                "audio channel count changed between segments");
        Require(MFGetAttributeUINT32(a, MF_MT_AUDIO_SAMPLES_PER_SECOND, 0) == MFGetAttributeUINT32(b, MF_MT_AUDIO_SAMPLES_PER_SECOND, 0),
                "audio sample rate changed between segments");
        Require(MFGetAttributeUINT32(a, MF_MT_AUDIO_BITS_PER_SAMPLE, 0) == MFGetAttributeUINT32(b, MF_MT_AUDIO_BITS_PER_SAMPLE, 0),
                "audio bit depth changed between segments");
        Require(MFGetAttributeUINT32(a, MF_MT_AUDIO_BLOCK_ALIGNMENT, 0) == MFGetAttributeUINT32(b, MF_MT_AUDIO_BLOCK_ALIGNMENT, 0),
                "audio block alignment changed between segments");
        RequireSameBlob(a, b);
    }
}

struct StreamTimeline
{
    bool Seen = false;
    LONGLONG FirstPts = 0;
    LONGLONG End = 0;
    LONGLONG LastDuration = 0;
    UINT64 Samples = 0;
    LONGLONG DecodeEnd = 0;
    DWORD SinkIndex = 0;
};

void SetDecodeTimestamp(IMFSample* sample, LONGLONG value)
{
    CheckHr(sample->SetUINT64(MFSampleExtension_DecodeTimestamp, static_cast<UINT64>(value)),
            "set decode timestamp");
}

std::array<int, 32> BuildStreamMap(const ComPtr<IMFSourceReader>& reader)
{
    std::array<int, 32> map{};
    map.fill(-1);
    for (DWORD index = 0; index < map.size(); ++index)
    {
        ComPtr<IMFMediaType> type;
        if (FAILED(reader->GetNativeMediaType(index, 0, &type))) continue;
        GUID major = GUID_NULL;
        if (FAILED(type->GetGUID(MF_MT_MAJOR_TYPE, &major))) continue;
        if (major == MFMediaType_Video) map[index] = 0;
        else if (major == MFMediaType_Audio) map[index] = 1;
    }
    return map;
}

void CopySegment(const ComPtr<IMFSourceReader>& reader,
                 const ComPtr<IMFSinkWriter>& writer,
                 const ProbeInfo& info,
                 std::array<StreamTimeline, 2>& timelines)
{
    auto map = BuildStreamMap(reader);
    std::array<bool, 2> seenInSegment{false, false};
    bool audioPrimingSkipped = false;
    std::array<LONGLONG, 2> nextDecodeTimestamp{timelines[0].DecodeEnd, timelines[1].DecodeEnd};
    const std::array<LONGLONG, 2> segmentOffsets{timelines[0].End, timelines[1].End};

    for (;;)
    {
        DWORD actualStream = 0, flags = 0;
        LONGLONG timestamp = 0;
        ComPtr<IMFSample> sample;
        HRESULT hr = reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_ANY_STREAM), 0, &actualStream, &flags,
                                        &timestamp, &sample);
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
        CheckHr(hr, "read compressed sample");
        if (flags & (MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED | MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED))
            throw std::runtime_error("media format changed while reading a segment");
        if (flags & MF_SOURCE_READERF_ERROR) CheckHr(hr, "source reader stream error");
        if (!sample || actualStream >= map.size() || map[actualStream] < 0) continue;

        int channel = map[actualStream];
        if ((channel == 0 && !info.HasVideo) || (channel == 1 && !info.HasAudio)) continue;
        auto& timeline = timelines[channel];

        // The MP4 source reader normalizes AAC priming to PTS=0, producing
        // two zero-PTS samples. Drop the first AAC packet in each segment.
        if (channel == 1 && !audioPrimingSkipped)
        {
            audioPrimingSkipped = true;
            continue;
        }
        LONGLONG pts = timestamp;
        LONGLONG duration = 0;
        CheckHr(sample->GetSampleDuration(&duration), "read sample duration");
        if (duration <= 0) duration = timeline.LastDuration;
        if (duration <= 0) duration = channel == 0 ? kTicksPerSecond / 30 : kTicksPerSecond / 100;

        if (!seenInSegment[channel])
        {
            timeline.FirstPts = pts;
            seenInSegment[channel] = true;
            if (channel == 0) CheckHr(sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE), "mark segment keyframe");
        }

        LONGLONG adjusted = segmentOffsets[channel] + pts - timeline.FirstPts;
        LONGLONG adjustedDts = nextDecodeTimestamp[channel];
        CheckHr(sample->SetSampleTime(adjusted), "adjust presentation timestamp");
        CheckHr(sample->SetSampleDuration(duration), "adjust sample duration");
        SetDecodeTimestamp(sample.Get(), adjustedDts);
        nextDecodeTimestamp[channel] += duration;
        timeline.DecodeEnd = nextDecodeTimestamp[channel];
        CheckHr(writer->WriteSample(timeline.SinkIndex, sample.Get()), "write compressed sample");

        timeline.End = std::max(timeline.End, adjusted + duration);
        timeline.LastDuration = duration;
        ++timeline.Samples;
    }

    Require(!info.HasVideo || seenInSegment[0], "segment contains no video samples");
    Require(!info.HasAudio || seenInSegment[1], "segment contains no audio samples");
}

void PrintProbe(const ProbeInfo& info)
{
    std::cout << std::fixed << std::setprecision(6);
    std::cout << "duration=" << static_cast<double>(info.Duration) / kTicksPerSecond << '\n';
    std::cout << "audio_streams=" << (info.HasAudio ? 1 : 0) << '\n';
    std::cout << "width=" << info.Width << '\n';
    std::cout << "height=" << info.Height << '\n';

    // Print the frame rate verbatim: the merge compatibility check used to hinge on this
    // value, and without it the only clue was "frame rate changed".
    if (info.HasVideo)
    {
        UINT64 frameRate = GetRatio(info.VideoType.Get(), MF_MT_FRAME_RATE, 0);
        std::cout << "frame_rate=" << (frameRate >> 32) << "/" << (frameRate & 0xffffffff) << '\n';
    }
}

int Probe(const std::wstring& path)
{
    ComPtr<IMFSourceReader> reader;
    auto info = OpenInput(path, reader);
    PrintProbe(info);
    return 0;
}

int Merge(const std::wstring& outputPath, const std::vector<std::wstring>& inputs)
{
    Require(inputs.size() >= 2, "merge requires at least two input segments");
    std::filesystem::path output(outputPath);
    std::filesystem::path temporary(outputPath + L".merge-tmp.mp4");
    std::error_code ec;
    std::filesystem::remove(temporary, ec);
    if (!output.parent_path().empty()) std::filesystem::create_directories(output.parent_path());

    ProbeInfo reference;
    ComPtr<IMFSinkWriter> writer;
    std::array<StreamTimeline, 2> timelines{};
    int frameRateMismatch = 0;

    try
    {
        for (size_t index = 0; index < inputs.size(); ++index)
        {
            ComPtr<IMFSourceReader> reader;
            ProbeInfo info = OpenInput(inputs[index], reader);
            if (index == 0)
            {
                reference = info;
                if (info.HasVideo)
                {
                    UINT64 frameRate = GetRatio(info.VideoType.Get(), MF_MT_FRAME_RATE, 0);
                    UINT32 numerator = static_cast<UINT32>(frameRate >> 32);
                    UINT32 denominator = static_cast<UINT32>(frameRate & 0xffffffff);
                    if (numerator > 0 && denominator > 0)
                    {
                        // Keep PTS >= DTS for every B frame without negative DTS.
                        // Shift both audio and video equally to preserve their sync.
                        LONGLONG prime = 2 * (kTicksPerSecond * denominator / numerator);
                        timelines[0].End = prime;
                        timelines[1].End = prime;
                    }
                }
                CheckHr(MFCreateSinkWriterFromURL(temporary.c_str(), nullptr, nullptr, &writer),
                        "MFCreateSinkWriterFromURL");
                if (info.HasVideo)
                {
                    CheckHr(writer->AddStream(info.VideoType.Get(), &timelines[0].SinkIndex), "add video stream");
                    CheckHr(writer->SetInputMediaType(timelines[0].SinkIndex, info.VideoType.Get(), nullptr),
                            "set compressed video input");
                }
                if (info.HasAudio)
                {
                    CheckHr(writer->AddStream(info.AudioType.Get(), &timelines[1].SinkIndex), "add audio stream");
                    CheckHr(writer->SetInputMediaType(timelines[1].SinkIndex, info.AudioType.Get(), nullptr),
                            "set compressed audio input");
                }
                CheckHr(writer->BeginWriting(), "begin output writing");
            }
            else
            {
                RequireCompatible(reference, info);
                if (reference.HasVideo && info.HasVideo
                    && GetRatio(reference.VideoType.Get(), MF_MT_FRAME_RATE, 0)
                       != GetRatio(info.VideoType.Get(), MF_MT_FRAME_RATE, 0))
                    ++frameRateMismatch;
            }
            CopySegment(reader, writer, info, timelines);
        }

        CheckHr(writer->Finalize(), "finalize merged MP4");
        writer.Reset();

        ComPtr<IMFSourceReader> resultReader;
        auto result = ProbeInfo(OpenInput(temporary.c_str(), resultReader));
        LONGLONG expected = std::max(timelines[0].End, timelines[1].End);
        Require(result.Duration > 0, "merged MP4 has no readable duration");
        Require(std::abs(result.Duration - expected) <= kTicksPerSecond,
                "merged MP4 duration differs from copied samples by more than one second");

        if (!MoveFileExW(temporary.c_str(), output.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
            throw std::runtime_error("replace output file failed");

        std::cout << "merge=ok\n";
        PrintProbe(result);
        std::cout << "video_samples=" << timelines[0].Samples << '\n';
        std::cout << "audio_samples=" << timelines[1].Samples << '\n';
        // Segments whose nominal frame rate differs: harmless, but worth reporting.
        std::cout << "frame_rate_mismatch=" << frameRateMismatch << '\n';
        return 0;
    }
    catch (...)
    {
        if (writer) writer->Finalize();
        std::filesystem::remove(temporary, ec);
        throw;
    }
}

void PrintUsage()
{
    std::wcerr << L"ScreenRec.Merger --probe input.mp4\n"
               << L"ScreenRec.Merger --merge output.mp4 part1.mp4 part2.mp4 [partN.mp4 ...]\n";
}
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 3)
    {
        PrintUsage();
        return 2;
    }

    HRESULT comHr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(comHr))
    {
        std::cerr << "COM initialization failed\n";
        return 3;
    }

    HRESULT mfHr = MFStartup(MF_VERSION);
    if (FAILED(mfHr))
    {
        CoUninitialize();
        std::cerr << "Media Foundation initialization failed\n";
        return 3;
    }

    int result = 0;
    try
    {
        std::wstring command = argv[1];
        if (command == L"--probe" && argc == 3)
        {
            result = Probe(argv[2]);
        }
        else if (command == L"--merge" && argc >= 5)
        {
            std::vector<std::wstring> inputs;
            for (int index = 3; index < argc; ++index) inputs.emplace_back(argv[index]);
            result = Merge(argv[2], inputs);
        }
        else
        {
            PrintUsage();
            result = 2;
        }
    }
    catch (const std::exception& error)
    {
        std::cerr << "error=" << error.what() << '\n';
        result = 5;
    }

    MFShutdown();
    CoUninitialize();
    return result;
}
