using grzyClothTool.Constants;
using System;
using System.ComponentModel;
using static grzyClothTool.Enums;

namespace grzyClothTool.Helpers;

public class SettingsHelper : INotifyPropertyChanged
{
    private static readonly Lazy<SettingsHelper> _instance = new(() => new SettingsHelper());
    public static SettingsHelper Instance => _instance.Value;

    public event PropertyChangedEventHandler PropertyChanged;

    private bool _displaySelectedDrawablePath;
    public bool DisplaySelectedDrawablePath
    {
        get => _displaySelectedDrawablePath;
        set => SetProperty(ref _displaySelectedDrawablePath, value, nameof(DisplaySelectedDrawablePath));
    }

    private int _polygonLimitHigh;
    public int PolygonLimitHigh
    {
        get => _polygonLimitHigh;
        set => SetProperty(ref _polygonLimitHigh, value, nameof(PolygonLimitHigh), revalidateDrawables: true);
    }

    private int _polygonLimitMed;
    public int PolygonLimitMed
    {
        get => _polygonLimitMed;
        set => SetProperty(ref _polygonLimitMed, value, nameof(PolygonLimitMed), revalidateDrawables: true);
    }

    private int _polygonLimitLow;
    public int PolygonLimitLow
    {
        get => _polygonLimitLow;
        set => SetProperty(ref _polygonLimitLow, value, nameof(PolygonLimitLow), revalidateDrawables: true);
    }

    private bool _autoDeleteFiles;
    public bool AutoDeleteFiles
    {
        get => _autoDeleteFiles;
        set => SetProperty(ref _autoDeleteFiles, value, nameof(AutoDeleteFiles));
    }

    private int _textureResolutionLimitDiffuse;
    public int TextureResolutionLimitDiffuse
    {
        get => _textureResolutionLimitDiffuse;
        set => SetProperty(ref _textureResolutionLimitDiffuse, value, nameof(TextureResolutionLimitDiffuse), revalidateDrawables: true);
    }

    private int _textureResolutionLimitNormal;
    public int TextureResolutionLimitNormal
    {
        get => _textureResolutionLimitNormal;
        set => SetProperty(ref _textureResolutionLimitNormal, value, nameof(TextureResolutionLimitNormal), revalidateDrawables: true);
    }

    private int _textureResolutionLimitSpecular;
    public int TextureResolutionLimitSpecular
    {
        get => _textureResolutionLimitSpecular;
        set => SetProperty(ref _textureResolutionLimitSpecular, value, nameof(TextureResolutionLimitSpecular), revalidateDrawables: true);
    }

    private int _maxDrawableNumber;

    public int MaxDrawableNumber
    {
        get => _maxDrawableNumber;
        set
        {
            var clamped = Math.Clamp(value, 0, GlobalConstants.MAX_DRAWABLE_NUMBER_LIMIT);
            if (_maxDrawableNumber != clamped)
            {
                _maxDrawableNumber = clamped;
                Properties.Settings.Default.MaxDrawablesPerAddon = clamped;
                Properties.Settings.Default.Save();
                OnPropertyChanged(nameof(MaxDrawableNumber));

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    MainWindow.AddonManager?.RedistributeDrawables();
                }));
            }
            else if (!Equals(value, clamped))
            {
                // Value was out of range and got clamped to the current value;
                // still notify so the UI snaps back to the clamped number.
                OnPropertyChanged(nameof(MaxDrawableNumber));
            }
        }
    }

    public static bool Preview3DAvailable { get; set; } = true;

    private SettingsHelper()
    {
        _displaySelectedDrawablePath = Properties.Settings.Default.DisplaySelectedDrawablePath;
        _polygonLimitHigh = Properties.Settings.Default.PolygonLimitHigh;
        _polygonLimitMed = Properties.Settings.Default.PolygonLimitMed;
        _polygonLimitLow = Properties.Settings.Default.PolygonLimitLow;
        _autoDeleteFiles = Properties.Settings.Default.AutoDeleteFiles;
        _markNewDrawables = Properties.Settings.Default.MarkNewDrawables;
        _drawableGroupingMode = (GroupingMode)Properties.Settings.Default.DrawableGroupingMode;
        if (_drawableGroupingMode == GroupingMode.None)
        {
            _drawableGroupingMode = GroupingMode.ByType; // the sidebar is always categorized now
        }
        _textureResolutionLimitDiffuse = Properties.Settings.Default.TextureResolutionLimitDiffuse;
        _textureResolutionLimitNormal = Properties.Settings.Default.TextureResolutionLimitNormal;
        _textureResolutionLimitSpecular = Properties.Settings.Default.TextureResolutionLimitSpecular;
        _maxDrawableNumber = Math.Clamp(Properties.Settings.Default.MaxDrawablesPerAddon, 0, GlobalConstants.MAX_DRAWABLE_NUMBER_LIMIT);
    }

    private void SetProperty<T>(ref T field, T value, string propertyName, bool revalidateDrawables = false)
    {
        if (!Equals(field, value))
        {
            field = value;
            Properties.Settings.Default[propertyName] = value;
            Properties.Settings.Default.Save();
            OnPropertyChanged(propertyName);

            if (revalidateDrawables)
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var addon in MainWindow.AddonManager.Addons)
                    {
                        foreach (var drawable in addon.Drawables)
                        {
                            foreach (var texture in drawable.Textures)
                            {
                                texture.TxtDetails?.Validate();
                            }
                            
                            foreach (var embeddedTexture in drawable.Details.EmbeddedTextures.Values)
                            {
                                embeddedTexture?.Details?.Validate();
                            }
                            
                            drawable.ValidateDetails();
                        }
                    }
                    
                    var selectedAddon = MainWindow.AddonManager.SelectedAddon;
                    if (selectedAddon?.SelectedDrawable != null)
                    {
                        var currentSelected = selectedAddon.SelectedDrawable;
                        selectedAddon.SelectedDrawable = null;
                        selectedAddon.SelectedDrawable = currentSelected;
                    }
                }));
            }
        }
    }

    private bool _markNewDrawables;
    public bool MarkNewDrawables
    {
        get => _markNewDrawables;
        set => SetProperty(ref _markNewDrawables, value, nameof(MarkNewDrawables));
    }

    private GroupingMode _drawableGroupingMode;
    public GroupingMode DrawableGroupingMode
    {
        get => _drawableGroupingMode;
        set
        {
            if (!Equals(_drawableGroupingMode, value))
            {
                _drawableGroupingMode = value;
                Properties.Settings.Default.DrawableGroupingMode = (int)value;
                Properties.Settings.Default.Save();
                OnPropertyChanged(nameof(DrawableGroupingMode));
            }
        }
    }

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
