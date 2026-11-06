using System;
using System.Drawing;
using System.IO;

namespace Hv86.Core
{
    /// <summary>
    /// Иконка приложения. Одна на exe, окна, трей и диалоги: рисуется
    /// tools\make-icon.ps1, вшивается в exe (assets\hv86.ico).
    ///
    /// Размер запрашивается явно: в 16 px нужна другая детализация, чем в
    /// 256, а Windows охотно берёт «первую попавшуюся» картинку из ico.
    /// </summary>
    public static class Brand
    {
        public const string Resource = "Hv86.Assets.hv86.ico";

        private static byte[] _bytes;
        private static bool _loaded;

        private static byte[] Bytes
        {
            get
            {
                if (!_loaded)
                {
                    _bytes = Resources.Bytes(Resource);
                    _loaded = true;
                }

                return _bytes;
            }
        }

        /// <summary>Иконка нужного размера. Если ресурса нет - системная.</summary>
        public static Icon Icon(int size)
        {
            try
            {
                byte[] bytes = Bytes;
                if (bytes != null && bytes.Length > 0)
                    return new Icon(new MemoryStream(bytes), new Size(size, size));
            }
            catch (Exception ex)
            {
                Log.Warn("BRAND", "иконка: " + ex.Message);
            }

            return SystemIcons.Application;
        }

        /// <summary>Кисть-заглушка для мест, где Icon не подходит (например, ImageList).</summary>
        public static Bitmap Bitmap(int size)
        {
            try
            {
                using (Icon icon = Icon(size))
                    return icon.ToBitmap();
            }
            catch
            {
                return new Bitmap(size, size);
            }
        }
    }
}