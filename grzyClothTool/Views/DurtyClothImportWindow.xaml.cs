using grzyClothTool.Helpers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace grzyClothTool.Views
{
    internal sealed class DurtyClothImportCandidate : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public DurtyClothImporter.DctProject Project { get; }

        public DurtyClothImportCandidate(DurtyClothImporter.DctProject project)
        {
            Project = project;
            _isSelected = true;

            if (project.Kind == DurtyClothImporter.SourceKind.Recovered)
            {
                // best guess for drawables whose gender isn't recorded anywhere
                var lower = (project.Name ?? string.Empty).ToLowerInvariant();
                _fallbackGender = project.KnownFemale > project.KnownMale || lower.Contains("female") || lower.Contains("fem")
                    ? "Female"
                    : "Male";
            }
        }

        public string Name => Project.Name;

        public string Detail
        {
            get
            {
                var source = Project.Kind switch
                {
                    DurtyClothImporter.SourceKind.ProjectFile => "Project file",
                    DurtyClothImporter.SourceKind.Recovered => "Recovered (no project file)",
                    _ => "Built resource"
                };

                var count = $"{Project.DrawableCount} drawable(s)";
                var genders = Project.Kind == DurtyClothImporter.SourceKind.Recovered
                    ? string.Empty
                    : $"  ·  {Project.Clothes.Count(c => !c.IsDummy && c.Sex == Enums.SexType.male)}M · {Project.Clothes.Count(c => !c.IsDummy && c.Sex == Enums.SexType.female)}F";

                return $"{source}  ·  {count}{genders}  ·  {Project.SourcePath}";
            }
        }

        public bool ShowGenderChoice => Project.Kind == DurtyClothImporter.SourceKind.Recovered;

        public List<string> GenderOptions { get; } = ["Male", "Female"];

        private string _fallbackGender = "Male";
        public string FallbackGender
        {
            get => _fallbackGender;
            set { _fallbackGender = value; OnPropertyChanged(); }
        }

        public Enums.SexType FallbackSex => FallbackGender == "Female" ? Enums.SexType.female : Enums.SexType.male;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public partial class DurtyClothImportWindow : Window
    {
        private readonly List<DurtyClothImportCandidate> _candidates;

        internal List<DurtyClothImportCandidate> SelectedCandidates => _candidates.Where(c => c.IsSelected).ToList();

        public bool CopyFiles => CopyFilesCheck.IsChecked == true;

        internal DurtyClothImportWindow(string root, IEnumerable<DurtyClothImporter.DctProject> projects)
        {
            InitializeComponent();

            _candidates = projects.Select(p => new DurtyClothImportCandidate(p)).ToList();
            foreach (var candidate in _candidates)
            {
                candidate.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DurtyClothImportCandidate.IsSelected))
                    {
                        UpdateSelectionText();
                    }
                };
            }

            ProjectList.ItemsSource = _candidates;

            var recovered = _candidates.Count(c => c.ShowGenderChoice);
            SubtitleText.Text = $"Found {_candidates.Count} project(s) in {root}." +
                (recovered > 0 ? $" {recovered} have no project file and will be rebuilt from DurtyCloth's cache." : string.Empty);

            UpdateSelectionText();
        }

        private void UpdateSelectionText()
        {
            var selected = _candidates.Where(c => c.IsSelected).ToList();
            SelectionText.Text = $"{selected.Count} of {_candidates.Count} selected  ·  {selected.Sum(c => c.Project.DrawableCount)} drawables";
            ImportButtonText.Text = selected.Count == 1 ? "Import 1 project" : $"Import {selected.Count} projects";
            ImportButton.IsEnabled = selected.Count > 0;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            _candidates.ForEach(c => c.IsSelected = true);
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            _candidates.ForEach(c => c.IsSelected = false);
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
