using QRCoder;
using ZXing;

namespace LanTodo.Core;

public static class InviteQr
{
    public static byte[] Png(string code)
    {
        using var data = QRCodeGenerator.GenerateQrCode(code, QRCodeGenerator.ECCLevel.M);
        using var renderer = new PngByteQRCode(data);
        return renderer.GetGraphic(6);
    }
    public static string? Decode(LuminanceSource pixels)
    {
        var reader = new BarcodeReaderGeneric { AutoRotate = true };
        reader.Options.TryHarder = true;
        reader.Options.PossibleFormats = [BarcodeFormat.QR_CODE];
        var value = reader.Decode(pixels)?.Text;
        return value?.StartsWith("lantodo2:") == true ? value : null;
    }
}
