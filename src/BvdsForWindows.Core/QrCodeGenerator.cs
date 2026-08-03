using QRCoder;

namespace BvdsForWindows.Core;

/// <summary>二维码生成（对应 Android ZXing QRCodeGenerator，改用 QRCoder）</summary>
public static class QrCodeGenerator
{
    /// <summary>生成二维码 PNG 字节（黑底白码）</summary>
    public static byte[]? GeneratePng(string text, int size)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            using var code = new PngByteQRCode(data);
            return code.GetGraphic(size);
        }
        catch
        {
            return null;
        }
    }
}
