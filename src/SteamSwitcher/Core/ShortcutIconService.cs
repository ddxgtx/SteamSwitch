using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    internal static class ShortcutIconService
    {
        private const int IconSize = 256;

        public static string? GetOrCreateIcon(string? sourcePath, string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return null;

            if (Path.GetExtension(sourcePath).Equals(".ico", StringComparison.OrdinalIgnoreCase))
                return sourcePath;

            try
            {
                var cacheDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SteamSwitch",
                    "ShortcutIcons");
                Directory.CreateDirectory(cacheDirectory);

                var safeKey = string.Concat(cacheKey.Select(character =>
                    Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
                var iconPath = Path.Combine(cacheDirectory, $"{safeKey}.ico");
                if (File.Exists(iconPath) &&
                    File.GetLastWriteTimeUtc(iconPath) >= File.GetLastWriteTimeUtc(sourcePath))
                {
                    return iconPath;
                }

                byte[] pngBytes = CreateSquarePng(sourcePath);
                var temporaryPath = $"{iconPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    WritePngIcon(temporaryPath, pngBytes);
                    File.Move(temporaryPath, iconPath, true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }

                return iconPath;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to create shortcut icon from {sourcePath}: {ex.Message}");
                return null;
            }
        }

        private static byte[] CreateSquarePng(string sourcePath)
        {
            BitmapDecoder decoder;
            using (var input = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                decoder = BitmapDecoder.Create(
                    input,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
            }

            BitmapSource source = decoder.Frames[0];
            int side = Math.Min(source.PixelWidth, source.PixelHeight);
            if (side <= 0)
                throw new InvalidDataException("图像尺寸无效。");

            int x = Math.Max(0, (source.PixelWidth - side) / 2);
            int y = Math.Max(0, (source.PixelHeight - side) / 2);
            BitmapSource square = new CroppedBitmap(source, new Int32Rect(x, y, side, side));
            if (side != IconSize)
            {
                square = new TransformedBitmap(
                    square,
                    new ScaleTransform(IconSize / (double)side, IconSize / (double)side));
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(square));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }

        private static void WritePngIcon(string destinationPath, byte[] pngBytes)
        {
            using var output = File.Create(destinationPath);
            using var writer = new BinaryWriter(output);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)pngBytes.Length);
            writer.Write((uint)22);
            writer.Write(pngBytes);
        }
    }
}
