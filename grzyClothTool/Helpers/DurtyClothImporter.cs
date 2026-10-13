using grzyClothTool.Constants;
using grzyClothTool.Models.Drawable;
using grzyClothTool.Models.Texture;
using grzyClothTool.Views;
using MessagePack;
using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using static grzyClothTool.Controls.CustomMessageBox;

namespace grzyClothTool.Helpers;

/// <summary>
/// Imports DurtyCloth work into Nightfall Clothing projects. Supported sources:
///  - .dctproj            (MessagePack + LZ4 block array, SaveVersion 5)
///  - *.durty-cloth.json  (older DurtyCloth versions, same schema as JSON)
///  - datacache.dat       (DurtyCloth's cache inside clothproject_*: lists every drawable and its
///                         ordered textures by MD5; type/gender come from validation text and the
///                         drawable's own internal name) - used when the project file is gone
///  - built resources     (stream folders with named mp_[mf]_freemode_01_*^type_000_u.ydd files)
/// Asset files are stored by GUID file name inside DurtyCloth's clothproject_*/data folders.
/// </summary>
public static class DurtyClothImporter
{
    // ================================================================== model

    internal enum SourceKind
    {
        ProjectFile,
        Recovered,
        BuiltResource
    }

    internal sealed class DctTexture
    {
        public int Position;
        public string FilePath;
        public bool IsDummy;
        public string Md5;
    }

    internal sealed class DctCloth
    {
        public bool IsProp;
        public int DrawableType = -1;
        public Enums.SexType Sex;
        public bool SexKnown = true;
        public bool HasSkin;
        public int Position;
        public bool IsDummy;
        public string MainPath;
        public string MainMd5;
        public string FirstPersonPath;
        public string PhysicsPath;
        public bool HighHeels;
        public float HighHeelHeight = 1.0f;
        public bool HideHair;
        public string OriginalFileName;
        public List<DctTexture> Textures = [];
    }

    internal sealed class DctProject
    {
        public string Name;
        public string SourcePath;
        public SourceKind Kind;
        public string DataFolder;
        public DateTime Modified;
        public List<DctCloth> Clothes = [];

        public int DrawableCount => Clothes.Count(c => !c.IsDummy);
        public int KnownFemale => Clothes.Count(c => c.SexKnown && c.Sex == Enums.SexType.female);
        public int KnownMale => Clothes.Count(c => c.SexKnown && c.Sex == Enums.SexType.male);
        public int UnknownSex => Clothes.Count(c => !c.IsDummy && !c.SexKnown);
    }

    internal sealed class ImportResult
    {
        public bool Success;
        public int Imported;
        public int Skipped;
    }

    private static readonly MessagePackSerializerOptions PackOptions = MessagePackSerializerOptions.Standard
        .WithCompression(MessagePackCompression.Lz4BlockArray)
        .WithSecurity(MessagePackSecurity.UntrustedData);

    // ================================================================== entry points

    /// <summary>Project → Import DurtyCloth Project File…: .dctproj / .durty-cloth.json, each in its own tab.</summary>
    public static async Task ImportWithPickerAsync(Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import DurtyCloth project file(s)",
            Filter = "DurtyCloth project (*.dctproj;*.durty-cloth.json)|*.dctproj;*.durty-cloth.json",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            await Models.ProjectTabs.OpenInNewTabAsync(() => ImportFileAsync(file, owner));
        }
    }

    /// <summary>Imports one project file into the active (empty) tab, asking for name/storage.</summary>
    public static async Task<bool> ImportFileAsync(string path, Window owner)
    {
        DctProject project;
        try
        {
            project = await Task.Run(() => ParseProjectFile(path));
        }
        catch (Exception ex)
        {
            LogHelper.Log($"Could not read DurtyCloth project '{Path.GetFileName(path)}': {ex.Message}", LogType.Error);
            Show($"Could not read this DurtyCloth project:\n\n{ex.Message}", "Import Failed", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Error);
            return false;
        }

        if (project.DrawableCount == 0)
        {
            Show("This DurtyCloth project has no clothing items.", "Nothing to Import", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Information);
            return false;
        }

        var index = await Task.Run(() => BuildFileIndex(Path.GetDirectoryName(path)));
        var missing = CountMissing(project, index);
        if (missing > 0)
        {
            var pick = Show(
                $"{missing} file(s) used by '{project.Name}' were not found next to the project file.\n\nSelect the DurtyCloth folder that contains this project's files?",
                "Locate Project Files",
                CustomMessageBoxButtons.OKCancel,
                CustomMessageBoxIcon.Warning);

            if (pick == CustomMessageBoxResult.OK)
            {
                var folderDialog = new OpenFolderDialog { Title = $"Select the folder containing '{project.Name}' files" };
                if (folderDialog.ShowDialog() == true)
                {
                    var extra = await Task.Run(() => BuildFileIndex(folderDialog.FolderName));
                    foreach (var kv in extra)
                    {
                        index.TryAdd(kv.Key, kv.Value);
                    }
                }
            }
        }

        var importable = project.Clothes.Count(c => !c.IsDummy && Resolve(index, c.MainPath) != null);
        var setup = ProjectSetupDialog.ShowForDurtyClothImport(owner, SanitizeName(project.Name), importable);
        if (!setup.Confirmed)
        {
            return false;
        }

        var projectName = setup.ProjectName.Trim();
        if (Models.ProjectTabs.Sessions.Any(s => s.Manager != MainWindow.AddonManager &&
            string.Equals(s.Manager.ProjectName?.Trim(), projectName, StringComparison.OrdinalIgnoreCase)))
        {
            Show($"A project named '{projectName}' is already open in another tab. Choose a different name.", "Project Already Open", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Warning);
            return false;
        }

        var result = await ImportIntoActiveTabAsync(project, index, projectName, !setup.IsSelfContained, Enums.SexType.male, quiet: false);
        return result.Success;
    }

    /// <summary>
    /// Project → Import DurtyCloth Folder…: scans a folder (one project or the whole DurtyCloth
    /// folder), finds every project - with or without its project file - and imports the ones picked.
    /// </summary>
    public static async Task ImportFolderAsync(Window owner)
    {
        var folderDialog = new OpenFolderDialog
        {
            Title = "Select a DurtyCloth folder (a single project or your whole DurtyCloth folder)"
        };

        if (folderDialog.ShowDialog() != true)
        {
            return;
        }

        var root = folderDialog.FolderName;

        ProgressHelper.Start($"Scanning {root} for DurtyCloth projects");
        ScanResult scan;
        try
        {
            scan = await Task.Run(() => Scan(root));
        }
        catch (Exception ex)
        {
            ProgressHelper.Stop("DurtyCloth scan failed", false);
            Show($"Could not scan this folder:\n\n{ex.Message}", "Import Failed", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Error);
            return;
        }
        ProgressHelper.Stop($"Found {scan.Projects.Count} DurtyCloth project(s) in {{0}}", true);

        if (scan.Projects.Count == 0)
        {
            Show("No DurtyCloth projects were found in this folder.\n\nPick the folder that contains your clothproject_* folders (or a project file).",
                "Nothing Found", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Information);
            return;
        }

        var window = new DurtyClothImportWindow(root, scan.Projects) { Owner = owner };
        if (window.ShowDialog() != true)
        {
            return;
        }

        var picked = window.SelectedCandidates;
        if (picked.Count == 0)
        {
            return;
        }

        bool keepTabsOpen = picked.Count <= 4;
        int projectsDone = 0, drawablesDone = 0, skippedTotal = 0;
        var failed = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in picked)
        {
            var name = UniqueProjectName(SanitizeName(candidate.Name), usedNames);
            usedNames.Add(name);

            ImportResult result = null;
            await Models.ProjectTabs.OpenInNewTabAsync(async () =>
            {
                result = await ImportIntoActiveTabAsync(candidate.Project, scan.Index, name, !window.CopyFiles, candidate.FallbackSex, quiet: true);
                return result.Success;
            });

            if (result?.Success == true)
            {
                projectsDone++;
                drawablesDone += result.Imported;
                skippedTotal += result.Skipped;

                if (!keepTabsOpen && Models.ProjectTabs.Active?.HasProject == true)
                {
                    await Models.ProjectTabs.CloseAsync(Models.ProjectTabs.Active);
                }
            }
            else
            {
                failed.Add(candidate.Name);
            }
        }

        var summary = $"Imported {projectsDone} project(s) with {drawablesDone} drawable(s).";
        if (!keepTabsOpen && projectsDone > 0)
        {
            summary += "\n\nThey were saved to your projects folder - open them from Recent on the home screen.";
        }
        if (skippedTotal > 0)
        {
            summary += $"\n\n{skippedTotal} item(s) were skipped (missing files or unknown type) - see Logs.";
        }
        if (failed.Count > 0)
        {
            summary += $"\n\nFailed: {string.Join(", ", failed)}";
        }

        Show(summary, "DurtyCloth Import", CustomMessageBoxButtons.OKOnly,
            failed.Count > 0 ? CustomMessageBoxIcon.Warning : CustomMessageBoxIcon.Information);
    }

    private static string UniqueProjectName(string baseName, HashSet<string> used)
    {
        var mainProjectsFolder = PersistentSettingsHelper.Instance.MainProjectsFolder;

        bool Taken(string n) =>
            used.Contains(n) ||
            Models.ProjectTabs.Sessions.Any(s => string.Equals(s.Manager.ProjectName?.Trim(), n, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(mainProjectsFolder) && Directory.Exists(Path.Combine(mainProjectsFolder, n)));

        if (!Taken(baseName))
        {
            return baseName;
        }

        for (int i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!Taken(candidate))
            {
                return candidate;
            }
        }
    }

    // ================================================================== import core

    private static async Task<ImportResult> ImportIntoActiveTabAsync(DctProject project, Dictionary<string, string> index, string projectName, bool isExternal, Enums.SexType fallbackSex, bool quiet)
    {
        var result = new ImportResult();
        var manager = MainWindow.AddonManager;

        ProgressHelper.Start($"Importing DurtyCloth project '{project.Name}'");

        try
        {
            if (project.Kind == SourceKind.Recovered)
            {
                await Task.Run(() => ResolveRecovered(project, fallbackSex));
            }

            manager.Addons.Clear();
            manager.Groups.Clear();
            manager.Tags.Clear();
            manager.ProjectName = projectName;
            manager.IsExternalProject = isExternal;
            DuplicateDetector.Clear();

            var drawables = new List<GDrawable>();

            var ordered = project.Clothes
                .Where(c => !c.IsDummy)
                .OrderBy(c => c.Sex)
                .ThenBy(c => c.IsProp)
                .ThenBy(c => c.DrawableType)
                .ThenBy(c => c.Position);

            foreach (var cloth in ordered)
            {
                if (!IsValidType(cloth))
                {
                    result.Skipped++;
                    LogHelper.Log($"DurtyCloth import: unknown clothing type for '{cloth.OriginalFileName ?? cloth.MainPath}' - skipped", LogType.Warning);
                    continue;
                }

                var ydd = Resolve(index, cloth.MainPath);
                if (ydd == null)
                {
                    result.Skipped++;
                    LogHelper.Log($"DurtyCloth import: missing drawable file for '{cloth.OriginalFileName ?? cloth.MainPath}'", LogType.Warning);
                    continue;
                }

                drawables.Add(await CreateDrawableAsync(cloth, ydd, index, isExternal));
            }

            manager.AddDrawablesBatch(drawables);

            if (manager.Addons.Count == 0)
            {
                manager.CreateAddon();
            }

            manager.SelectedAddon = manager.Addons.First();
            result.Imported = drawables.Count;

            ProgressHelper.Stop($"Imported '{project.Name}' ({drawables.Count} drawables) in {{0}}", true);

            if (!quiet && result.Skipped > 0)
            {
                Show($"Imported {drawables.Count} drawable(s). {result.Skipped} item(s) were skipped (missing files or unknown type) - see Logs for details.",
                    "Import Finished", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Warning);
            }

            SaveHelper.SetUnsavedChanges(true);
            await SaveHelper.SaveAsync();

            var mainProjectsFolder = PersistentSettingsHelper.Instance.MainProjectsFolder;
            if (!string.IsNullOrEmpty(mainProjectsFolder))
            {
                PersistentSettingsHelper.Instance.AddRecentProject(
                    Path.Combine(mainProjectsFolder, projectName, SaveHelper.GetSaveFileName(isExternal)),
                    projectName,
                    drawables.Count,
                    manager.Addons.Count,
                    isExternal: isExternal);
            }

            result.Success = drawables.Count > 0;
            return result;
        }
        catch (Exception ex)
        {
            LogHelper.Log($"DurtyCloth import of '{project.Name}' failed: {ex.Message}", LogType.Error);
            ProgressHelper.Stop("DurtyCloth import failed", false);
            if (!quiet)
            {
                Show($"Import failed:\n\n{ex.Message}", "Import Failed", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Error);
            }
            return result;
        }
    }

    private static async Task<GDrawable> CreateDrawableAsync(DctCloth cloth, string yddPath, Dictionary<string, string> index, bool isExternal)
    {
        var drawableId = Guid.NewGuid();
        var drawablePath = await PlaceFileAsync(yddPath, drawableId, isExternal);

        var textures = new ObservableCollection<GTexture>();
        int txtNumber = 0;
        foreach (var tex in cloth.Textures.Where(t => !t.IsDummy).OrderBy(t => t.Position))
        {
            if (txtNumber >= GlobalConstants.MAX_DRAWABLE_TEXTURES)
            {
                break;
            }

            var texSource = Resolve(index, tex.FilePath);
            if (texSource == null)
            {
                LogHelper.Log($"DurtyCloth import: missing texture {tex.FilePath ?? tex.Md5} for '{cloth.OriginalFileName ?? cloth.MainPath}'", LogType.Warning);
                continue;
            }

            var texId = Guid.NewGuid();
            var texPath = await PlaceFileAsync(texSource, texId, isExternal);
            textures.Add(new GTexture(texId, texPath, cloth.DrawableType, 0, txtNumber, cloth.HasSkin, cloth.IsProp));
            txtNumber++;
        }

        var drawable = new GDrawable(drawableId, drawablePath, cloth.Sex, cloth.IsProp, cloth.DrawableType, 0, cloth.HasSkin, textures);

        var firstPerson = Resolve(index, cloth.FirstPersonPath);
        if (firstPerson != null)
        {
            drawable.FirstPersonPath = await PlaceFileAsync(firstPerson, Guid.NewGuid(), isExternal);
        }

        var physics = Resolve(index, cloth.PhysicsPath);
        if (physics != null)
        {
            drawable.ClothPhysicsPath = await PlaceFileAsync(physics, Guid.NewGuid(), isExternal);
        }

        if (cloth.HighHeels)
        {
            drawable.HighHeelsValue = cloth.HighHeelHeight;
            drawable.EnableHighHeels = true;
        }

        if (cloth.HideHair)
        {
            drawable.HidesHair = true;
        }

        return drawable;
    }

    private static async Task<string> PlaceFileAsync(string source, Guid id, bool isExternal)
    {
        if (isExternal)
        {
            return source;
        }

        try
        {
            return await FileHelper.CopyToProjectAssetsAsync(source, id.ToString());
        }
        catch (Exception ex)
        {
            LogHelper.Log($"Could not copy {Path.GetFileName(source)} into the project ({ex.Message}); referencing it in place.", LogType.Warning);
            return source;
        }
    }

    private static bool IsValidType(DctCloth cloth)
    {
        if (cloth.DrawableType < 0)
        {
            return false;
        }

        var enumType = cloth.IsProp ? typeof(Enums.PropNumbers) : typeof(Enums.ComponentNumbers);
        return Enum.IsDefined(enumType, cloth.DrawableType);
    }

    internal static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "durtycloth_import";
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Trim();
    }

    // ================================================================== scanning

    internal sealed class ScanResult
    {
        public List<DctProject> Projects = [];
        public Dictionary<string, string> Index = new(StringComparer.OrdinalIgnoreCase);
    }

    private static ScanResult Scan(string root)
    {
        var result = new ScanResult();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };

        var projectFiles = new List<string>();
        var caches = new List<string>();
        var streamDirs = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            var name = Path.GetFileName(file);
            var ext = Path.GetExtension(name).ToLowerInvariant();

            if (ext is ".ydd" or ".ytd" or ".yld")
            {
                result.Index.TryAdd(name, file);
            }
            else if (ext == ".dctproj" || name.EndsWith(".durty-cloth.json", StringComparison.OrdinalIgnoreCase))
            {
                projectFiles.Add(file);
            }
            else if (name.Equals("datacache.dat", StringComparison.OrdinalIgnoreCase))
            {
                caches.Add(file);
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(root, "stream", options))
        {
            streamDirs.Add(dir);
        }

        // ---- 1) project files: newest usable file per project name wins
        var parsed = new List<(DctProject Project, int Resolvable, HashSet<string> DataFolders)>();
        foreach (var file in projectFiles)
        {
            try
            {
                var project = ParseProjectFile(file);
                var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int resolvable = 0;
                foreach (var cloth in project.Clothes.Where(c => !c.IsDummy))
                {
                    var path = Resolve(result.Index, cloth.MainPath);
                    if (path != null)
                    {
                        resolvable++;
                        folders.Add(Path.GetDirectoryName(path));
                    }
                }

                if (resolvable > 0)
                {
                    parsed.Add((project, resolvable, folders));
                }
            }
            catch (Exception ex)
            {
                LogHelper.Log($"Skipped unreadable DurtyCloth project file '{file}': {ex.Message}", LogType.Warning);
            }
        }

        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in parsed.GroupBy(p => p.Project.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var best = group
                .OrderByDescending(p => p.Project.Modified)
                .ThenByDescending(p => p.Resolvable)
                .First();

            result.Projects.Add(best.Project);
            foreach (var folder in group.SelectMany(p => p.DataFolders))
            {
                covered.Add(folder);
            }
        }

        // ---- 2) recovered from datacache.dat (project file missing)
        foreach (var cache in caches)
        {
            var dataFolder = Path.Combine(Path.GetDirectoryName(cache)!, "data");
            if (!Directory.Exists(dataFolder) || covered.Contains(dataFolder))
            {
                continue;
            }

            try
            {
                var project = ParseDataCache(cache, dataFolder);
                if (project.DrawableCount > 0)
                {
                    result.Projects.Add(project);
                    covered.Add(dataFolder);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Log($"Skipped unreadable DurtyCloth cache '{cache}': {ex.Message}", LogType.Warning);
            }
        }

        // ---- 3) built resources (stream folders) not already covered
        foreach (var stream in streamDirs)
        {
            var resourceDir = Path.GetDirectoryName(stream)!;
            var clothProjectDir = Path.GetDirectoryName(resourceDir);
            if (clothProjectDir != null && covered.Contains(Path.Combine(clothProjectDir, "data")))
            {
                continue;
            }

            try
            {
                var project = ParseStreamFolder(stream);
                if (project.DrawableCount > 0)
                {
                    result.Projects.Add(project);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Log($"Skipped DurtyCloth build folder '{stream}': {ex.Message}", LogType.Warning);
            }
        }

        result.Projects = result.Projects
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }

    // ================================================================== files

    private static Dictionary<string, string> BuildFileIndex(string root)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return index;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 };

        foreach (var pattern in new[] { "*.ydd", "*.ytd", "*.yld" })
        {
            foreach (var file in Directory.EnumerateFiles(root, pattern, options))
            {
                index.TryAdd(Path.GetFileName(file), file);
            }
        }

        return index;
    }

    private static string Resolve(Dictionary<string, string> index, string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return null;
        }

        if (Path.IsPathRooted(storedPath) && File.Exists(storedPath))
        {
            return storedPath;
        }

        return index.TryGetValue(Path.GetFileName(storedPath), out var full) ? full : null;
    }

    private static int CountMissing(DctProject project, Dictionary<string, string> index)
    {
        int missing = 0;
        foreach (var cloth in project.Clothes.Where(c => !c.IsDummy))
        {
            if (Resolve(index, cloth.MainPath) == null) missing++;
            missing += cloth.Textures.Count(t => !t.IsDummy && Resolve(index, t.FilePath) == null);
        }
        return missing;
    }

    // ================================================================== project files (.dctproj / .json)

    private static DctProject ParseProjectFile(string path)
    {
        IDictionary root;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true });
            root = ToObject(doc.RootElement) as IDictionary
                ?? throw new InvalidDataException("Unrecognised DurtyCloth JSON layout.");
        }
        else
        {
            root = MessagePackSerializer.Deserialize<object>(File.ReadAllBytes(path), PackOptions) as IDictionary
                ?? throw new InvalidDataException("Unrecognised .dctproj layout (root is not a map).");
        }

        var project = new DctProject
        {
            Name = GetString(root, "ProjectName") ?? GetString(root, "DlcName") ?? Path.GetFileNameWithoutExtension(path),
            SourcePath = path,
            Kind = SourceKind.ProjectFile,
            Modified = File.GetLastWriteTimeUtc(path)
        };

        foreach (var item in GetList(root, "ClothData"))
        {
            if (item is not IDictionary c)
            {
                continue;
            }

            var cloth = new DctCloth
            {
                IsProp = GetInt(c, "ClothType") == 1,
                DrawableType = GetInt(c, "DrawableType"),
                Sex = GetInt(c, "TargetGender") == 1 ? Enums.SexType.female : Enums.SexType.male,
                HasSkin = GetInt(c, "DrawableSkinType") == 1,
                Position = GetInt(c, "Position"),
                IsDummy = GetBool(c, "IsDummy"),
                MainPath = GetString(c, "MainPath"),
                FirstPersonPath = GetString(c, "FirstPersonModelFilePath"),
                PhysicsPath = GetString(c, "MeshPhysicsFilePath"),
                OriginalFileName = GetString(c, "OriginalFileName") ?? GetString(c, "MainPath")
            };

            if (cloth.IsProp)
            {
                cloth.HasSkin = false;
            }

            if (Get(c, "PedComponentFlags") is IDictionary flags)
            {
                cloth.HighHeels = GetBool(flags, "Flag5");
                var height = GetFloat(flags, "HighHeelHeight");
                if (height > 0) cloth.HighHeelHeight = height;
            }

            if (Get(c, cloth.IsProp ? "PedPropOptions" : "PedComponentOptions") is IDictionary pedOptions)
            {
                cloth.HideHair = GetBool(pedOptions, "HideHairs");
            }

            foreach (var t in GetList(c, "Textures"))
            {
                if (t is IDictionary td)
                {
                    cloth.Textures.Add(new DctTexture
                    {
                        Position = GetInt(td, "Position"),
                        FilePath = GetString(td, "FilePath"),
                        IsDummy = GetBool(td, "IsDummy")
                    });
                }
            }

            project.Clothes.Add(cloth);
        }

        return project;
    }

    private static object ToObject(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, object>();
                foreach (var property in element.EnumerateObject())
                {
                    map[property.Name] = ToObject(property.Value);
                }
                return map;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(ToObject).ToList();
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetInt64(out var l) ? l : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    // ================================================================== recovery from datacache.dat

    // 'Female 3 Top' (Top) at pos 3
    private static readonly Regex CacheMessageRegex = new(@"'(Male|Female) (\d+) [^']*' \(([^)]+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static DctProject ParseDataCache(string cachePath, string dataFolder)
    {
        var root = MessagePackSerializer.Deserialize<object>(File.ReadAllBytes(cachePath), PackOptions) as object[]
            ?? throw new InvalidDataException("Unrecognised datacache layout.");

        var entries = root.Length > 0 ? root[0] as object[] : null;
        if (entries == null)
        {
            throw new InvalidDataException("Unrecognised datacache layout.");
        }

        var clothProjectDir = Path.GetDirectoryName(cachePath)!;
        var name = Path.GetFileName(clothProjectDir);
        if (name.StartsWith("clothproject_", StringComparison.OrdinalIgnoreCase))
        {
            name = name["clothproject_".Length..];
        }

        var project = new DctProject
        {
            Name = name,
            SourcePath = cachePath,
            Kind = SourceKind.Recovered,
            DataFolder = dataFolder,
            Modified = File.GetLastWriteTimeUtc(cachePath)
        };

        int order = 0;
        foreach (var entryObj in entries)
        {
            if (entryObj is not object[] entry || entry.Length < 2 || entry[1] is not object[] drawable || drawable.Length < 2)
            {
                continue;
            }

            var cloth = new DctCloth
            {
                MainMd5 = drawable[1] as string,
                SexKnown = false,
                Position = 100000 + order++
            };

            // validation messages carry "'Female 3 Top' (Top) at pos 3"
            if (drawable.Length > 4 && drawable[4] is object[] messages)
            {
                foreach (var m in messages.OfType<object[]>())
                {
                    if (m.Length > 2 && m[2] is string text)
                    {
                        var match = CacheMessageRegex.Match(text);
                        if (match.Success)
                        {
                            cloth.Sex = match.Groups[1].Value.Equals("female", StringComparison.OrdinalIgnoreCase) ? Enums.SexType.female : Enums.SexType.male;
                            cloth.SexKnown = true;
                            cloth.Position = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                            var type = CategoryLabelToType(match.Groups[3].Value);
                            if (type.HasValue)
                            {
                                cloth.IsProp = type.Value.IsProp;
                                cloth.DrawableType = type.Value.Type;
                            }
                            break;
                        }
                    }
                }
            }

            if (drawable.Length > 5 && drawable[5] is object[] textures)
            {
                int position = 0;
                foreach (var t in textures.OfType<object[]>())
                {
                    if (t.Length > 1 && t[1] is string md5)
                    {
                        cloth.Textures.Add(new DctTexture { Position = position++, Md5 = md5 });
                    }
                }
            }

            project.Clothes.Add(cloth);
        }

        return project;
    }

    /// <summary>Matches cache entries to files by MD5 and reads type/gender from each drawable.</summary>
    private static void ResolveRecovered(DctProject project, Enums.SexType fallbackSex)
    {
        var byHash = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(project.DataFolder)
            .Where(f => f.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, file =>
        {
            try
            {
                using var stream = File.OpenRead(file);
                var hash = Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
                byHash.TryAdd(hash, file);
            }
            catch
            {
                // unreadable file - ignore
            }
        });

        foreach (var cloth in project.Clothes)
        {
            if (cloth.MainMd5 != null && byHash.TryGetValue(cloth.MainMd5, out var ydd))
            {
                cloth.MainPath = ydd;
                cloth.OriginalFileName = Path.GetFileName(ydd);

                var sniff = SniffDrawable(ydd);
                if (sniff != null)
                {
                    if (cloth.DrawableType < 0)
                    {
                        cloth.IsProp = sniff.Value.IsProp;
                        cloth.DrawableType = sniff.Value.Type;
                    }

                    cloth.HasSkin = !cloth.IsProp && sniff.Value.Skin;

                    if (!cloth.SexKnown && sniff.Value.Sex.HasValue)
                    {
                        cloth.Sex = sniff.Value.Sex.Value;
                        cloth.SexKnown = true;
                    }

                    if (cloth.Position >= 100000 && sniff.Value.Number >= 0)
                    {
                        cloth.Position = sniff.Value.Number;
                    }
                }
            }

            foreach (var texture in cloth.Textures)
            {
                if (texture.Md5 != null && byHash.TryGetValue(texture.Md5, out var ytd))
                {
                    texture.FilePath = ytd;
                }
            }
        }

        // gender: known genders in this project first, otherwise the user's fallback
        var known = project.Clothes.Where(c => c.SexKnown).Select(c => c.Sex).Distinct().ToList();
        var defaultSex = known.Count == 1 ? known[0] : fallbackSex;
        foreach (var cloth in project.Clothes.Where(c => !c.SexKnown))
        {
            cloth.Sex = defaultSex;
            cloth.SexKnown = true;
        }
    }

    private static readonly string[] ComponentNames = ["head", "berd", "hair", "uppr", "lowr", "hand", "feet", "teef", "accs", "task", "decl", "jbib"];
    private static readonly string[] PropNames = ["p_head", "p_eyes", "p_ears", "p_mouth", "p_lhand", "p_rhand", "p_lwrist", "p_rwrist", "p_hip", "p_lfoot", "p_rfoot"];

    private static readonly Regex ComponentNameRegex = new(
        @"(" + string.Join("|", ComponentNames) + @")_(\d{3})_([ur])(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PropNameRegex = new(
        @"(?:\^|\b)(" + string.Join("|", PropNames) + @")_(\d{3})(?!_?(?:diff|normal|spec|\d))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly record struct SniffResult(bool IsProp, int Type, int Number, bool Skin, Enums.SexType? Sex);

    /// <summary>Reads the drawable's internal name (e.g. "jbib_007_u") from the YDD resource.</summary>
    private static SniffResult? SniffDrawable(string yddPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(yddPath);
            string text;

            if (bytes.Length > 16 && bytes[0] == (byte)'R' && bytes[1] == (byte)'S' && bytes[2] == (byte)'C' && bytes[3] == (byte)'7')
            {
                using var input = new MemoryStream(bytes, 16, bytes.Length - 16);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                deflate.CopyTo(output);
                text = Encoding.Latin1.GetString(output.GetBuffer(), 0, (int)output.Length);
            }
            else
            {
                text = Encoding.Latin1.GetString(bytes);
            }

            Enums.SexType? sex = null;
            if (text.Contains("mp_f_freemode", StringComparison.OrdinalIgnoreCase)) sex = Enums.SexType.female;
            else if (text.Contains("mp_m_freemode", StringComparison.OrdinalIgnoreCase)) sex = Enums.SexType.male;

            var component = ComponentNameRegex.Match(text);
            if (component.Success)
            {
                return new SniffResult(
                    false,
                    Array.IndexOf(ComponentNames, component.Groups[1].Value.ToLowerInvariant()),
                    int.Parse(component.Groups[2].Value, CultureInfo.InvariantCulture),
                    component.Groups[3].Value.Equals("r", StringComparison.OrdinalIgnoreCase),
                    sex);
            }

            var prop = PropNameRegex.Match(text);
            if (prop.Success)
            {
                return new SniffResult(
                    true,
                    Array.IndexOf(PropNames, prop.Groups[1].Value.ToLowerInvariant()),
                    int.Parse(prop.Groups[2].Value, CultureInfo.InvariantCulture),
                    false,
                    sex);
            }
        }
        catch
        {
            // unreadable resource - type stays unknown
        }

        return null;
    }

    /// <summary>DurtyCloth category label ("Top", "Legs", "Undershirt", "Hat"…) → slot.</summary>
    private static (bool IsProp, int Type)? CategoryLabelToType(string label)
    {
        var l = label.Trim().ToLowerInvariant();

        if (l.Contains("undershirt")) return (false, (int)Enums.ComponentNumbers.accs);
        if (l.Contains("top")) return (false, (int)Enums.ComponentNumbers.jbib);
        if (l.Contains("leg") || l.Contains("pant") || l.Contains("trouser")) return (false, (int)Enums.ComponentNumbers.lowr);
        if (l.Contains("shoe") || l.Contains("feet")) return (false, (int)Enums.ComponentNumbers.feet);
        if (l.Contains("armor") || l.Contains("armour") || l.Contains("vest")) return (false, (int)Enums.ComponentNumbers.task);
        if (l.Contains("body") || l.Contains("torso") || l.Contains("glove")) return (false, (int)Enums.ComponentNumbers.uppr);
        if (l.Contains("bag") || l.Contains("parachute")) return (false, (int)Enums.ComponentNumbers.hand);
        if (l.Contains("hair")) return (false, (int)Enums.ComponentNumbers.hair);
        if (l.Contains("mask") || l.Contains("beard")) return (false, (int)Enums.ComponentNumbers.berd);
        if (l.Contains("decal") || l.Contains("badge")) return (false, (int)Enums.ComponentNumbers.decl);
        if (l.Contains("accessor") || l.Contains("neck") || l.Contains("chain") || l.Contains("teef") || l.Contains("scarf")) return (false, (int)Enums.ComponentNumbers.teef);

        if (l.Contains("hat") || l.Contains("helmet")) return (true, (int)Enums.PropNumbers.p_head);
        if (l.Contains("glass")) return (true, (int)Enums.PropNumbers.p_eyes);
        if (l.Contains("ear")) return (true, (int)Enums.PropNumbers.p_ears);
        if (l.Contains("mouth")) return (true, (int)Enums.PropNumbers.p_mouth);
        if (l.Contains("watch")) return (true, (int)Enums.PropNumbers.p_lwrist);
        if (l.Contains("bracelet")) return (true, (int)Enums.PropNumbers.p_rwrist);
        if (l.Contains("hip")) return (true, (int)Enums.PropNumbers.p_hip);
        if (l.Contains("left hand")) return (true, (int)Enums.PropNumbers.p_lhand);
        if (l.Contains("right hand")) return (true, (int)Enums.PropNumbers.p_rhand);
        if (l.Contains("left foot")) return (true, (int)Enums.PropNumbers.p_lfoot);
        if (l.Contains("right foot")) return (true, (int)Enums.PropNumbers.p_rfoot);

        if (l == "head" || l.Contains("face")) return (false, (int)Enums.ComponentNumbers.head);

        return null;
    }

    // ================================================================== built resources (stream folders)

    // mp_f_freemode_01_mp_f_name^jbib_000_u.ydd  |  mp_m_freemode_01_p_mp_m_name^p_head_000.ydd
    private static readonly Regex StreamDrawableRegex = new(
        @"^(?<ped>mp_[mf])_freemode_01(?<rest>[^\^]*)\^(?<type>[a-z_]+?)_(?<num>\d{3})(?:_(?<skin>[ur]))?(?<fp>_1)?\.(?<ext>ydd|yld)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StreamTextureRegex = new(
        @"^(?<ped>mp_[mf])_freemode_01(?<rest>[^\^]*)\^(?<type>[a-z_]+?)_diff_(?<num>\d{3})_(?<letter>[a-z])(?:_(?:uni|whi))?\.ytd$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static DctProject ParseStreamFolder(string streamDir)
    {
        var resourceDir = Path.GetDirectoryName(streamDir)!;
        var project = new DctProject
        {
            Name = Path.GetFileName(resourceDir),
            SourcePath = streamDir,
            Kind = SourceKind.BuiltResource,
            Modified = Directory.GetLastWriteTimeUtc(streamDir)
        };

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 };
        var files = Directory.EnumerateFiles(streamDir, "*", options).ToList();

        var clothes = new Dictionary<string, DctCloth>(StringComparer.OrdinalIgnoreCase);
        var extras = new List<(string Key, string Path, bool FirstPerson, bool Physics)>();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var match = StreamDrawableRegex.Match(name);
            if (!match.Success)
            {
                continue;
            }

            var typeName = match.Groups["type"].Value.ToLowerInvariant();
            bool isProp = typeName.StartsWith("p_");
            int type = isProp ? Array.IndexOf(PropNames, typeName) : Array.IndexOf(ComponentNames, typeName);
            if (type < 0)
            {
                continue;
            }

            var key = $"{match.Groups["ped"].Value}{match.Groups["rest"].Value}|{typeName}|{match.Groups["num"].Value}".ToLowerInvariant();
            var isPhysics = match.Groups["ext"].Value.Equals("yld", StringComparison.OrdinalIgnoreCase);
            var isFirstPerson = match.Groups["fp"].Success;

            if (isPhysics || isFirstPerson)
            {
                extras.Add((key, file, isFirstPerson, isPhysics));
                continue;
            }

            clothes[key] = new DctCloth
            {
                IsProp = isProp,
                DrawableType = type,
                Sex = match.Groups["ped"].Value.EndsWith("f", StringComparison.OrdinalIgnoreCase) ? Enums.SexType.female : Enums.SexType.male,
                HasSkin = !isProp && match.Groups["skin"].Value.Equals("r", StringComparison.OrdinalIgnoreCase),
                Position = int.Parse(match.Groups["num"].Value, CultureInfo.InvariantCulture),
                MainPath = file,
                OriginalFileName = name
            };
        }

        foreach (var extra in extras)
        {
            if (clothes.TryGetValue(extra.Key, out var cloth))
            {
                if (extra.Physics) cloth.PhysicsPath = extra.Path;
                if (extra.FirstPerson) cloth.FirstPersonPath = extra.Path;
            }
        }

        foreach (var file in files)
        {
            var match = StreamTextureRegex.Match(Path.GetFileName(file));
            if (!match.Success)
            {
                continue;
            }

            var key = $"{match.Groups["ped"].Value}{match.Groups["rest"].Value}|{match.Groups["type"].Value}|{match.Groups["num"].Value}".ToLowerInvariant();
            if (clothes.TryGetValue(key, out var cloth))
            {
                cloth.Textures.Add(new DctTexture
                {
                    Position = char.ToLowerInvariant(match.Groups["letter"].Value[0]) - 'a',
                    FilePath = file
                });
            }
        }

        project.Clothes.AddRange(clothes.Values);
        return project;
    }

    // ================================================================== map helpers

    private static object Get(IDictionary map, string key)
    {
        return map.Contains(key) ? map[key] : null;
    }

    private static string GetString(IDictionary map, string key)
    {
        return Get(map, key) as string;
    }

    private static int GetInt(IDictionary map, string key)
    {
        var value = Get(map, key);
        return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static float GetFloat(IDictionary map, string key)
    {
        var value = Get(map, key);
        return value == null ? 0f : Convert.ToSingle(value, CultureInfo.InvariantCulture);
    }

    private static bool GetBool(IDictionary map, string key)
    {
        return Get(map, key) is bool b && b;
    }

    private static IEnumerable GetList(IDictionary map, string key)
    {
        return Get(map, key) is IEnumerable list and not string ? list : Array.Empty<object>();
    }
}
