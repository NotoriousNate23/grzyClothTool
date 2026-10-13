using grzyClothTool.Helpers;
using grzyClothTool.Models.Drawable;
using grzyClothTool.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace grzyClothTool.Models
{
    /// <summary>
    /// One open project (one tab). Each tab keeps its own AddonManager, its own cached
    /// ProjectWindow and its own duplicate index, so switching tabs is instant: nothing
    /// is reloaded or rebuilt.
    /// </summary>
    public class ProjectSession : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public AddonManager Manager { get; }

        internal ProjectWindow View { get; set; }
        internal Dictionary<string, List<GDrawable>> DuplicateState { get; set; } = [];

        public ProjectSession(AddonManager manager)
        {
            Manager = manager;
        }

        public bool HasProject => !string.IsNullOrWhiteSpace(Manager.ProjectName) || Manager.Addons.Count > 0;

        public string Title => string.IsNullOrWhiteSpace(Manager.ProjectName)
            ? (Manager.Addons.Count > 0 ? "Untitled" : "Home")
            : Manager.ProjectName;

        private bool _hasUnsavedChanges;
        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            set { if (_hasUnsavedChanges != value) { _hasUnsavedChanges = value; OnPropertyChanged(); } }
        }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { if (_isActive != value) { _isActive = value; OnPropertyChanged(); } }
        }

        public void RefreshTitle()
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(HasProject));
        }

        public string GetSaveFilePath()
        {
            var mainProjectsFolder = PersistentSettingsHelper.Instance.MainProjectsFolder;
            if (string.IsNullOrWhiteSpace(mainProjectsFolder) || string.IsNullOrWhiteSpace(Manager.ProjectName))
            {
                return null;
            }

            return Path.Combine(mainProjectsFolder, Manager.ProjectName.Trim(), SaveHelper.GetSaveFileName(Manager.IsExternalProject));
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// Multi-project tabs. MainWindow.AddonManager always points at the active tab's manager,
    /// so all existing code keeps working against "the current project".
    /// </summary>
    public static class ProjectTabs
    {
        public static ObservableCollection<ProjectSession> Sessions { get; } = [];

        public static ProjectSession Active { get; private set; }

        public static event Action ActiveChanged;

        /// <summary>Creates the first (empty) tab at startup.</summary>
        public static AddonManager Initialize()
        {
            var session = new ProjectSession(new AddonManager()) { IsActive = true };
            Sessions.Add(session);
            Active = session;
            return session.Manager;
        }

        public static bool AnyUnsavedChanges()
        {
            return Sessions.Any(s => s == Active ? SaveHelper.HasUnsavedChanges : s.HasUnsavedChanges);
        }

        /// <summary>Opens a new empty tab showing the home screen (Ctrl+T / "+").</summary>
        public static void NewTab()
        {
            // reuse an already-open empty tab instead of stacking empty ones
            var empty = Sessions.FirstOrDefault(s => !s.HasProject);
            if (empty != null)
            {
                Activate(empty);
                return;
            }

            var session = new ProjectSession(new AddonManager());
            Sessions.Add(session);
            Activate(session);
        }

        /// <summary>
        /// If the given save file is already open in a tab, switches to it (closing the current
        /// empty tab) and returns true. Prevents two tabs editing and autosaving the same project.
        /// </summary>
        public static bool TryActivateExisting(string saveFilePath)
        {
            if (string.IsNullOrWhiteSpace(saveFilePath))
            {
                return false;
            }

            var match = Sessions.FirstOrDefault(s =>
                s.HasProject &&
                string.Equals(s.GetSaveFilePath(), saveFilePath, StringComparison.OrdinalIgnoreCase));

            return SwitchToMatch(match);
        }

        public static bool TryActivateExistingByName(string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectName))
            {
                return false;
            }

            var match = Sessions.FirstOrDefault(s =>
                s.HasProject &&
                string.Equals(s.Manager.ProjectName?.Trim(), projectName.Trim(), StringComparison.OrdinalIgnoreCase));

            return SwitchToMatch(match);
        }

        private static bool SwitchToMatch(ProjectSession match)
        {
            if (match == null)
            {
                return false;
            }

            var current = Active;
            Activate(match);

            if (current != null && current != match && !current.HasProject)
            {
                RemoveSession(current);
            }

            return true;
        }

        /// <summary>
        /// Runs a project load in a fresh tab when the current tab already holds a project.
        /// On cancel/failure the empty tab is discarded and the previous tab comes back.
        /// </summary>
        public static async Task<bool> OpenInNewTabAsync(Func<Task<bool>> load)
        {
            var previous = Active;
            if (previous != null && previous.HasProject)
            {
                NewTab();
            }

            bool ok;
            try
            {
                ok = await load();
            }
            catch (Exception ex)
            {
                LogHelper.Log($"Opening project failed: {ex.Message}", LogType.Error);
                ok = false;
            }

            if (ok)
            {
                Active?.RefreshTitle();
                ShowActive();
                return true;
            }

            if (previous != null && Active != previous && Active != null && !Active.HasProject)
            {
                var empty = Active;
                Activate(previous);
                RemoveSession(empty);
            }

            return false;
        }

        public static void ActivateNext(int direction = 1)
        {
            if (Sessions.Count < 2 || Active == null)
            {
                return;
            }

            var index = Sessions.IndexOf(Active);
            var next = (index + direction + Sessions.Count) % Sessions.Count;
            Activate(Sessions[next]);
        }

        public static void Activate(ProjectSession target)
        {
            if (target == null || target == Active)
            {
                return;
            }

            // imports/builds write into "the current project"; never swap it underneath them
            if (SaveHelper.SavingPaused)
            {
                LogHelper.Log("Wait for the current operation to finish before switching projects.", LogType.Warning);
                return;
            }

            var outgoing = Active;

            // ---- park the outgoing tab ----
            if (outgoing != null)
            {
                outgoing.HasUnsavedChanges = SaveHelper.HasUnsavedChanges;
                outgoing.DuplicateState = DuplicateDetector.DetachState();
                outgoing.IsActive = false;
                outgoing.RefreshTitle();

                if (outgoing.HasUnsavedChanges && outgoing.HasProject)
                {
                    _ = SaveInBackgroundAsync(outgoing);
                }
            }

            // ---- bring in the target tab ----
            Active = target;
            target.IsActive = true;
            MainWindow.SetActiveAddonManager(target.Manager);
            DuplicateDetector.AttachState(target.DuplicateState);
            GroupManager.Instance.NotifyProjectChanged();
            SaveHelper.SetUnsavedChanges(target.HasUnsavedChanges);

            ShowActive();
            SyncPreview(target);

            ActiveChanged?.Invoke();
        }

        /// <summary>Returns the cached project view of the active tab, creating it once.</summary>
        public static ProjectWindow GetOrCreateActiveView()
        {
            if (Active == null)
            {
                return new ProjectWindow();
            }

            Active.View ??= new ProjectWindow();
            Active.RefreshTitle();
            return Active.View;
        }

        public static void ShowActive()
        {
            if (Active != null && Active.HasProject)
            {
                MainWindow.NavigationHelper.Navigate("Project");
            }
            else
            {
                MainWindow.NavigationHelper.Navigate("Home");
            }
        }

        public static async Task CloseAsync(ProjectSession session)
        {
            if (session == null || !Sessions.Contains(session))
            {
                return;
            }

            if (SaveHelper.SavingPaused)
            {
                LogHelper.Log("Wait for the current operation to finish before closing a project.", LogType.Warning);
                return;
            }

            var isActive = session == Active;
            var unsaved = isActive ? SaveHelper.HasUnsavedChanges : session.HasUnsavedChanges;

            if (unsaved && session.HasProject)
            {
                var saved = await SaveHelper.SaveManagerAsync(session.Manager);
                if (!saved && !SaveHelper.CheckUnsavedChangesMessage())
                {
                    return;
                }
            }

            if (isActive)
            {
                // the tab being closed must not leave its unsaved flag on the next tab
                SaveHelper.HasUnsavedChanges = false;
            }

            if (Sessions.Count == 1)
            {
                // last tab: replace it with a fresh home tab
                var fresh = new ProjectSession(new AddonManager());
                Sessions.Add(fresh);
                Activate(fresh);
            }
            else if (isActive)
            {
                var index = Sessions.IndexOf(session);
                var neighbour = index > 0 ? Sessions[index - 1] : Sessions[index + 1];
                Activate(neighbour);
            }

            RemoveSession(session);
        }

        private static void RemoveSession(ProjectSession session)
        {
            if (session == Active)
            {
                return;
            }

            Sessions.Remove(session);
            session.View = null;
            session.DuplicateState = null;
            session.Manager.Shutdown();
        }

        private static async Task SaveInBackgroundAsync(ProjectSession session)
        {
            try
            {
                if (await SaveHelper.SaveManagerAsync(session.Manager))
                {
                    MainWindow.Instance?.Dispatcher.Invoke(() => session.HasUnsavedChanges = false);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Log($"Background save of '{session.Title}' failed: {ex.Message}", LogType.Warning);
            }
        }

        private static void SyncPreview(ProjectSession target)
        {
            try
            {
                var mainWindow = MainWindow.Instance;
                if (mainWindow?.PreviewAnchorable?.IsVisible != true || mainWindow.PreviewHost == null)
                {
                    return;
                }

                target.Manager.IsPreviewEnabled = true;
                var addon = target.Manager.SelectedAddon;
                var selection = addon?.SelectedDrawables ?? new ObservableCollection<GDrawable>();
                mainWindow.PreviewHost.UpdateDrawables(selection, addon?.SelectedTexture, []);
            }
            catch (Exception ex)
            {
                LogHelper.Log($"Could not refresh 3D preview for tab: {ex.Message}", LogType.Warning);
            }
        }
    }
}
