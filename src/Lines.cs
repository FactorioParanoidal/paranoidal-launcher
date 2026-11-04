using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Paranoidal
{
    /// <summary>
    /// One line of the build: Paranoidal for one Factorio version (major.minor). The launcher serves one line at a time
    /// (settings.ini line=2.0|2.1, Lines.Current); a player of the 2.0 line moves to 2.1 with «Paranoidal 2.1»
    /// (Migration.cs). README «Линии сборки».
    /// </summary>
    sealed class BuildLine
    {
        /// <summary>"2.0" / "2.1": major.minor of the game and the factorio_version of the build's mods.</summary>
        public readonly string Id;
        public readonly string Branch;
        /// <summary>The game version the build needs when its own requirement is not known (offline, nothing installed).</summary>
        public readonly string DefaultMin;
        /// <summary>2.0: exactly DefaultMin. 2.1: the version zzzparanoidal requires (base >= x.y.z) or newer within 2.1.</summary>
        public readonly bool Exact;
        /// <summary>Expansion mods in data\ that must have the game's version.</summary>
        public readonly string[] Dlc;
        /// <summary>DLC entries of mod-list.json and the state the build needs (other entries are never touched).</summary>
        public readonly KeyValuePair<string, bool>[] DlcStates;
        string required;

        public BuildLine(string id, string branch, string defaultMin, bool exact, string[] dlc, KeyValuePair<string, bool>[] states)
        {
            Id = id;
            Branch = branch;
            DefaultMin = defaultMin;
            Exact = exact;
            Dlc = dlc;
            DlcStates = states;
        }

        /// <summary>The oldest game version the build needs.</summary>
        public string MinGame => Exact || required == null || Releases.Compare(required, DefaultMin) <= 0 ? DefaultMin : required;

        /// <summary>The requirement of the build (zzzparanoidal: base >= x.y.z) installed or about to be installed.</summary>
        public void SetRequired(string version)
        {
            if (Releases.IsVersion(version) && Matches(version)) required = version;
        }

        /// <summary>A game version ("2.1.24") or a factorio_version ("2.1") of this line.</summary>
        public bool Matches(string version) => MajorMinor(version) == Id;

        /// <summary>The build of this line runs on this game version.</summary>
        public bool Fits(string gameVersion) =>
            gameVersion != null && (Exact ? gameVersion == DefaultMin : Matches(gameVersion) && Releases.Compare(gameVersion, MinGame) >= 0);

        /// <summary>The game the build needs, worded the same everywhere it is shown: "Factorio 2.1.24+ Space Age".</summary>
        public string RequiredGame => "Factorio " + MinGame + (Exact ? "" : "+") + " Space Age";

        /// <summary>"exactly 2.0.77" / "2.1.24 or newer (2.1.x)" in the interface language (en = null) or the given one.</summary>
        public string VersionWords(bool? en = null)
        {
            bool e = en ?? Tr.En;
            if (Exact) return (e ? "exactly " : "строго ") + MinGame;
            return MinGame + (e ? " or newer (" : " или новее (") + Id + ".x)";
        }

        public string Name => "Paranoidal " + Id;

        public static string MajorMinor(string version)
        {
            var m = Regex.Match(version ?? "", @"^\s*(\d+)\.(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) + "." + int.Parse(m.Groups[2].Value) : null;
        }
    }

    static class Lines
    {
        public static readonly BuildLine L20 = new BuildLine("2.0", "pre_release_2.0", "2.0.77", true,
            new[] { "space-age", "quality", "elevated-rails" },
            new[]
            {
                new KeyValuePair<string, bool>("space-age", false),
                new KeyValuePair<string, bool>("quality", true),
                new KeyValuePair<string, bool>("elevated-rails", true),
            });

        /// <summary>Quality 2.1 depends on the recycler mod, so it is switched on too (ПР-069 of the modpack).</summary>
        public static readonly BuildLine L21 = new BuildLine("2.1", "pre_release_2.1", "2.1.24", false,
            new[] { "space-age", "quality", "elevated-rails", "recycler" },
            new[]
            {
                new KeyValuePair<string, bool>("space-age", false),
                new KeyValuePair<string, bool>("quality", true),
                new KeyValuePair<string, bool>("elevated-rails", true),
                new KeyValuePair<string, bool>("recycler", true),
            });

        public static readonly BuildLine[] All = { L20, L21 };

        static BuildLine current;

        /// <summary>
        /// The line of this launcher: settings.ini line=…; without it 2.1 (a new player gets 2.1 as if there were no 2.0;
        /// MainForm.Startup decides for players of 1.2.0 and older before anything uses it).
        /// </summary>
        public static BuildLine Current
        {
            get { return current ?? (current = ById(Settings.Get("line")) ?? L21); }
        }

        public static void Set(BuildLine line) { current = line; }

        public static BuildLine ById(string id) => id == "2.0" ? L20 : id == "2.1" ? L21 : null;

        /// <summary>The line of a factorio_version / game version ("2.0", "2.1.24"); null for other versions.</summary>
        public static BuildLine Of(string version)
        {
            var mm = BuildLine.MajorMinor(version);
            foreach (var l in All) if (l.Id == mm) return l;
            return null;
        }
    }
}
