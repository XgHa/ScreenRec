namespace ScreenRec.Core;

/// <summary>
/// 编码器偏好。自动模式由 ScreenRecorderLib 优先使用硬件编码，X264 对应纯软件编码。
/// </summary>
public enum EncoderChoice { Auto, Nvenc, Qsv, Amf, X264 }
