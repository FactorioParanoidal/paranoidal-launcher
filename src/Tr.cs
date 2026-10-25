using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Paranoidal
{
    /// <summary>
    /// Interface language (RU/EN). Strings are kept in place as pairs: Tr.T("русский", "English").
    /// The journal, sync-log.txt and crash.txt stay Russian whatever the interface language is (the modpack team reads them).
    /// Default: Russian when Windows uses a language of a post-Soviet country or the user's region is one of them;
    /// otherwise English. The choice is stored in settings.ini (lang=ru|en).
    /// </summary>
    static class Tr
    {
        public static bool En { get; private set; }

        /// <summary>Raised on the UI thread after the language changed.</summary>
        public static event Action Changed;

        public static string T(string ru, string en) => En ? en : ru;

        public static void Init()
        {
            var s = Settings.Get("lang");
            if (s == "en") En = true;
            else if (s == "ru") En = false;
            else En = !PreferRussian();
        }

        public static void Set(bool en)
        {
            if (En == en) return;
            En = en;
            Settings.Set("lang", en ? "en" : "ru");
            Changed?.Invoke();
        }

        /// <summary>Languages of the 15 post-Soviet countries (ro-MD is checked separately).</summary>
        static readonly string[] Languages = { "ru", "uk", "be", "kk", "uz", "ky", "tg", "tk", "az", "hy", "ka", "lt", "lv", "et" };
        static readonly string[] Regions = { "RU", "UA", "BY", "KZ", "UZ", "KG", "TJ", "TM", "AZ", "AM", "GE", "MD", "LT", "LV", "EE" };

        static bool PreferRussian()
        {
            try
            {
                var ui = CultureInfo.CurrentUICulture;
                if (Array.IndexOf(Languages, ui.TwoLetterISOLanguageName) >= 0) return true;
                if (ui.Name.Equals("ro-MD", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            try { if (Array.IndexOf(Regions, new RegionInfo(CultureInfo.CurrentCulture.Name).TwoLetterISORegionName) >= 0) return true; }
            catch { }
            try
            {
                var geo = GetUserGeoID(16);   // GEOCLASS_NATION
                var sb = new StringBuilder(8);
                if (geo != -1 && GetGeoInfo(geo, 4, sb, sb.Capacity, 0) > 0 && Array.IndexOf(Regions, sb.ToString().ToUpperInvariant()) >= 0)
                    return true;   // 4 = GEO_ISO2
            }
            catch { }
            return false;
        }

        [DllImport("kernel32.dll")] static extern int GetUserGeoID(int geoClass);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetGeoInfo(int location, int geoType, StringBuilder data, int size, int langId);
    }
}
