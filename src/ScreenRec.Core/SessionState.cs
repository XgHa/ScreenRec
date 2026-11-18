namespace ScreenRec.Core;

public enum SessionState { Idle, Recording, Paused, Finalizing }

/// <summary>录制完成结果。Segments &gt; 1 表示经过暂停/恢复,由多个分段合并而来。</summary>
public sealed record RecordingResult(string VideoPath, long Bytes, TimeSpan Duration, int Segments = 1);
