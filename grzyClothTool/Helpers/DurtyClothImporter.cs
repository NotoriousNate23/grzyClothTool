using grzyClothTool.Constants;
using grzyClothTool.Models.Drawable;
using grzyClothTool.Models.Texture;
using grzyClothTool.Views;
using MessagePack;
using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using static grzyClothTool.Controls.CustomMessageBox;

namespace grzyClothTool.Helpers;

/// <summary>
/// Imports DurtyCloth projects (.dctproj).
///
/// Format: MessagePack-CSharp, LZ4 block-array compressed, string-keyed maps (SaveVersion 5).
/// Root: ProjectName, DlcName, ClothData[]. Each cloth entry:
///   ClothType (0 component / 1 prop), DrawableType (GTA component or prop slot index),
///   TargetGender (0 male / 1 female), DrawableSkinType (1 = _r skin), Position (order),
///   IsDummy (reserved slot), MainPath (GUID .ydd), Textures[] (FilePath GUID .ytd, Position),
///   FirstPersonModelFilePath, MeshPhysicsFilePath, PedComponentFlags / PedComponentOptions /
///   PedPropOptions. Asset files are stored by GUID file name inside DurtyCloth's project folder.
/// </summary>
public static class DurtyClothImporter
{
    private sealed class DctTexture
    {
        public int Position;
        public string FilePath;
        public bool IsDummy;
    }

    private sealed class DctCloth
    {
        public bool IsProp;
        public int DrawableType;
        public Enums.SexType Sex;
        public bool HasSkin;
        public int Position;
        public bool IsDummy;
        public string MainPath;
        public string FirstPersonPath;
        public string PhysicsPath;
        public bool HighHeels;
        public float HighHeelHeight = 1.0f;
        public bool HideHair;
        public string OriginalFileName;
        public List<DctTexture> Textures = [];
    }

    private sealed class DctProject
    {
        public string Name;
        public List<DctCloth> Clothes = [];
    }

    /// <summary>Project → Import DurtyCloth Project…: each selected file opens in its own tab.</summary>
    public static async Task ImportWithPickerAsync(Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import DurtyCloth project(s)",
            Filter = "DurtyCloth project (*.dctproj)|*.dctproj",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            await Models.ProjectTabs.OpenInNewTabAsync(() => ImportAsync(file, owner));
        }
    }

    /// <summary>Imports one .dctproj into the active (empty) tab.</summary>
    public static async Task<bool> ImportAsync(string dctprojPath, Window owner)
    {
        DctProject project;
        try
        {
            project = await Task.Run(() => Parse(dctprojPath));
        }
        catch (Exception ex)
        {
            LogHelper.Log($"Could not read DurtyCloth project '{Path.GetFileName(dctprojPath)}': {ex.Message}", LogType.Error);
            Show($"Could not read this DurtyCloth project:\n\n{ex.Message}", "Import Failed", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Error);
            return false;
        }

        if (project.Clothes.Count == 0)
        {
            Show("This DurtyCloth project has no clothing items.", "Nothing to Import", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Information);
            return false;
        }

        // ---- locate the GUID-named asset files ----
        var index = await Task.Run(() => BuildFileIndex(Path.GetDirectoryName(dctprojPath)));
        var missing = CountMissing(project, index);
        if (missing > 0)
        {
            var pick = Show(
                $"{missing} file(s) used by '{project.Name}' were not found next to the .dctproj.\n\nSelect the DurtyCloth folder that contains this project's files?",
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
                    missing = CountMissing(project, index);
                }
            }
        }

        var importable = project.Clothes.Count(c => !c.IsDummy && Resolve(index, c.MainPath) != null);
        var setup = ProjectSetupDialog.ShowForDurtyClothImport(owner, SanitizeName(project.Name), importable);
        if (!setup.Confirmed)
        {
            return false;
        }

        var manager = MainWindow.AddonManager;
        var projectName = setup.ProjectName.Trim();
        var isExternal = !setup.IsSelfContained;

        if (Models.ProjectTabs.Sessions.Any(s => s.Manager != manager &&
            string.Equals(s.Manager.ProjectName?.Trim(), projectName, StringComparison.OrdinalIgnoreCase)))
        {
            Show($"A project named '{projectName}' is already open in another tab. Choose a different name.", "Project Already Open", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Warning);
            return false;
        }

        ProgressHelper.Start($"Importing DurtyCloth project '{project.Name}'");

        try
        {
            manager.Addons.Clear();
            manager.Groups.Clear();
            manager.Tags.Clear();
            manager.ProjectName = projectName;
            manager.IsExternalProject = isExternal;
            DuplicateDetector.Clear();

            var drawables = new List<GDrawable>();
            int skipped = 0;

            var ordered = project.Clothes
                .OrderBy(c => c.Sex)
                .ThenBy(c => c.IsProp)
                .ThenBy(c => c.DrawableType)
                .ThenBy(c => c.Position);

            foreach (var cloth in ordered)
            {
                if (!IsValidType(cloth))
                {
                    skipped++;
                    continue;
                }

                if (cloth.IsDummy)
                {
                    continue; // DurtyCloth dummy slots are not imported as reserved drawables
                }

                var ydd = Resolve(index, cloth.MainPath);
                if (ydd == null)
                {
                    skipped++;
                    LogHelper.Log($"DurtyCloth import: missing drawable file for '{cloth.OriginalFileName}' ({cloth.MainPath})", LogType.Warning);
                    continue;
                }

                var drawable = await CreateDrawableAsync(cloth, ydd, index, isExternal);
                drawables.Add(drawable);
            }

            manager.AddDrawablesBatch(drawables);

            if (manager.Addons.Count == 0)
            {
                manager.CreateAddon();
            }

            manager.SelectedAddon = manager.Addons.First();

            ProgressHelper.Stop($"Imported '{project.Name}' ({drawables.Count} drawables) in {{0}}", true);

            if (skipped > 0)
            {
                Show($"Imported {drawables.Count} drawable(s). {skipped} item(s) were skipped because their files could not be found - see Logs for details.",
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

            return true;
        }
        catch (Exception ex)
        {
            LogHelper.Log($"DurtyCloth import failed: {ex.Message}", LogType.Error);
            ProgressHelper.Stop("DurtyCloth import failed", false);
            Show($"Import failed:\n\n{ex.Message}", "Import Failed", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Error);
            return false;
        }
    }

    // ------------------------------------------------------------------ building

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
                LogHelper.Log($"DurtyCloth import: missing texture {tex.FilePath} for '{cloth.OriginalFileName}'", LogType.Warning);
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
        var enumType = cloth.IsProp ? typeof(Enums.PropNumbers) : typeof(Enums.ComponentNumbers);
        return Enum.IsDefined(enumType, cloth.DrawableType);
    }

    private static string SanitizeName(string name)
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

    // ------------------------------------------------------------------ files

    private static Dictionary<string, string> BuildFileIndex(string root)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return index;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 6
        };

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

    // ------------------------------------------------------------------ parsing

    private static DctProject Parse(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var options = MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.Lz4BlockArray)
            .WithSecurity(MessagePackSecurity.UntrustedData);

        var root = MessagePackSerializer.Deserialize<object>(bytes, options) as IDictionary
            ?? throw new InvalidDataException("Unrecognised .dctproj layout (root is not a map).");

        var project = new DctProject
        {
            Name = GetString(root, "ProjectName") ?? GetString(root, "DlcName") ?? Path.GetFileNameWithoutExtension(path)
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

            var options2 = Get(c, cloth.IsProp ? "PedPropOptions" : "PedComponentOptions") as IDictionary;
            if (options2 != null)
            {
                cloth.HideHair = GetBool(options2, "HideHairs");
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
        return Get(map, key) as IEnumerable ?? Array.Empty<object>();
    }
}
