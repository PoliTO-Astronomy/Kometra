using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Kometra.Models.Nodes;

namespace Kometra.ViewModels.Nodes;

public class GraphNodeViewModel : BaseNodeViewModel
{
    private readonly GraphNodeModel _model;
    private List<Bitmap> _graphImages = new();
    private int _currentIndex = 0;

    public List<string> ImagePaths => _model.ImagePaths;
    public string CsvPath => _model.CsvPath;
    public string AccentColor { get; set; } = "#FF8C00";

    public Bitmap? CurrentGraphImage => _graphImages.Count > 0 && _currentIndex >= 0 && _currentIndex < _graphImages.Count 
        ? _graphImages[_currentIndex] 
        : null;

    public int TotalCount => _graphImages.Count;
    public bool HasMultipleGraphs => TotalCount > 1;

    public int CurrentIndex 
    { 
        get => _currentIndex; 
        set 
        { 
            if (SetProperty(ref _currentIndex, Math.Clamp(value, 0, Math.Max(0, TotalCount - 1))))
            {
                OnPropertyChanged(nameof(CurrentGraphImage));
                OnPropertyChanged(nameof(CurrentImageText));
                OnPropertyChanged(nameof(CanMoveNext));
                OnPropertyChanged(nameof(CanMovePrevious));
            }
        }
    }

    public string CurrentImageText => TotalCount > 0 ? $"{_currentIndex + 1} / {TotalCount}" : "0 / 0";
    
    public bool CanMoveNext => _currentIndex < TotalCount - 1;
    public bool CanMovePrevious => _currentIndex > 0;

    public Size NodeContentSize { get; set; } = new Size(1500, 1125);

    public GraphNodeViewModel(GraphNodeModel model) : base(model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        Title = string.IsNullOrWhiteSpace(_model.Title) ? "Grafico Dati" : _model.Title;
    }

    public async Task InitializeAsync()
    {
        try
        {
            if (_model.ImagePaths != null && _model.ImagePaths.Any())
            {
                await Task.Run(() =>
                {
                    var list = new List<Bitmap>();
                    foreach (var path in _model.ImagePaths)
                    {
                        if (File.Exists(path))
                        {
                            using var stream = File.OpenRead(path);
                            list.Add(new Bitmap(stream));
                        }
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        _graphImages = list;
                        OnPropertyChanged(nameof(TotalCount));
                        OnPropertyChanged(nameof(HasMultipleGraphs));
                        OnPropertyChanged(nameof(CurrentGraphImage));
                        OnPropertyChanged(nameof(CurrentImageText));
                        OnPropertyChanged(nameof(CanMoveNext));
                        OnPropertyChanged(nameof(CanMovePrevious));
                    });
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Errore caricamento immagini grafici: {ex.Message}");
        }
    }

    public void Next() => CurrentIndex++;
    public void Previous() => CurrentIndex--;
    public void First() => CurrentIndex = 0;
    public void Last() => CurrentIndex = TotalCount - 1;

    public new void Dispose()
    {
        foreach (var bmp in _graphImages) bmp?.Dispose();
        _graphImages.Clear();
        base.Dispose();
    }
}