using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kometra.ViewModels;
using Kometra.ViewModels.Nodes;

namespace Kometra.Views;

public partial class GraphNodeView : UserControl
{
    private Point? _startDragPoint;
    private BoardViewModel? _cachedBoardVm;
    private Visual? _cachedBoardView;

    public GraphNodeView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _cachedBoardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
        _cachedBoardVm = _cachedBoardView?.DataContext as BoardViewModel;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cachedBoardVm = null;
        _cachedBoardView = null;
        _startDragPoint = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not GraphNodeViewModel nodeVm) return;
        
        var properties = e.GetCurrentPoint(this).Properties;
        var textBox = this.FindControl<TextBox>("TitleTextBox");
        var headerBorder = this.FindControl<Border>("TitleHeader");
        
        var sourceVisual = e.Source as Visual;

        bool isHeaderClick = headerBorder != null && sourceVisual != null && 
                             (sourceVisual == headerBorder || headerBorder.IsVisualAncestorOf(sourceVisual));

        if (isHeaderClick && (sourceVisual is Button || sourceVisual.GetVisualAncestors().OfType<Button>().Any())) 
            isHeaderClick = false;

        if (e.ClickCount == 2 && properties.IsLeftButtonPressed)
        {
            if (isHeaderClick && textBox != null)
            {
                textBox.IsReadOnly = false;
                textBox.Focus();
                textBox.SelectAll();
                e.Handled = true; 
                return;
            }
        }

        if (properties.IsLeftButtonPressed)
        {
            if (textBox != null && !textBox.IsReadOnly)
            {
                if (!isHeaderClick && !textBox.IsVisualAncestorOf(sourceVisual))
                {
                    textBox.IsReadOnly = true;
                    this.Focus();
                }
                else return; 
            }

            bool isModifier = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || 
                              e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                              e.KeyModifiers.HasFlag(KeyModifiers.Meta);

            if (!nodeVm.IsSelected || isModifier)
            {
                _cachedBoardVm?.SetSelectedNode(nodeVm, isModifier);
            }
            else if (_cachedBoardVm != null && _cachedBoardVm.SelectedNodesCount == 2)
            {
                if (_cachedBoardVm.SelectedNodes.IndexOf(nodeVm) != 0)
                {
                    _cachedBoardVm.SelectedNodes.Remove(nodeVm);
                    _cachedBoardVm.SelectedNodes.Insert(0, nodeVm);
                }
            }
            
            nodeVm.BringToFront(); 

            if (_cachedBoardView != null)
            {
                _startDragPoint = e.GetPosition(_cachedBoardView); 
                
                if (_cachedBoardVm != null)
                {
                    foreach (var n in _cachedBoardVm.SelectedNodes)
                    {
                        n.VisualOffsetX = 0;
                        n.VisualOffsetY = 0;
                    }
                }

                e.Pointer.Capture(this); 
                e.Handled = true; 
            }
        }
    }
    
    private void TitleTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox)
        {
            textBox.IsReadOnly = true;
            this.Focus(); 
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && sender is TextBox tb)
        {
            tb.IsReadOnly = true;
            this.Focus();
            e.Handled = true;
        }
    }

    private void TitleTextBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.IsReadOnly = true;
            textBox.SelectionStart = 0;
            textBox.SelectionEnd = 0;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_startDragPoint == null || _cachedBoardView == null || _cachedBoardVm == null) return;
        
        var currentPosBoard = e.GetPosition(_cachedBoardView);
        var screenDelta = currentPosBoard - _startDragPoint.Value;

        double scale = _cachedBoardVm.Viewport.Scale;
        if (scale <= 0.01) scale = 0.1; 

        double worldDeltaX = screenDelta.X / scale;
        double worldDeltaY = screenDelta.Y / scale;

        foreach (var n in _cachedBoardVm.SelectedNodes)
        {
            n.VisualOffsetX = worldDeltaX;
            n.VisualOffsetY = worldDeltaY;
        }
        
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_startDragPoint != null && e.InitialPressMouseButton == MouseButton.Left)
        {
            if (_cachedBoardVm != null)
            {
                foreach (var n in _cachedBoardVm.SelectedNodes)
                {
                    n.X += n.VisualOffsetX;
                    n.Y += n.VisualOffsetY;
                    
                    n.VisualOffsetX = 0;
                    n.VisualOffsetY = 0;
                }
            }

            _startDragPoint = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
}