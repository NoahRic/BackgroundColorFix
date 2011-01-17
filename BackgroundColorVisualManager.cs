using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Linq;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Classification;
using System.Collections.Generic;
using System;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text.Tagging;

namespace BackgroundColorFix
{
    public class BackgroundColorVisualManager
    {
        IAdornmentLayer _layer;
        IWpfTextView _view;
        ITagAggregator<ClassificationTag> _aggregator;
        IClassificationFormatMap _formatMap;
        IVsFontsAndColorsInformationService _fcService;
        IVsEditorAdaptersFactoryService _adaptersService;

        bool _inUpdate = false;

        // There's a bug in Visual Studio 2010 RTM that prevents us from using an opacity of 0.0.  To work around
        // this, we just a value very close to 0.0 for transparent.
        const double Transparent = 0.00000001;

        // This is the normal background opacity of classification format definitions that are picked up from
        // the fonts and colors table.
        const double BackgroundOpacity = 0.8;

        public BackgroundColorVisualManager(IWpfTextView view, ITagAggregator<ClassificationTag> aggregator, IClassificationFormatMap formatMap,
                                            IVsFontsAndColorsInformationService fcService, IVsEditorAdaptersFactoryService adaptersService)
        {
            _view = view;
            _layer = view.GetAdornmentLayer("BackgroundColorFix");
            _aggregator = aggregator;
            _formatMap = formatMap;

            _fcService = fcService;
            _adaptersService = adaptersService;

            _view.LayoutChanged += OnLayoutChanged;

            // Here are the hacks for making the normal classification background go away:
            FixFormatMap(_formatMap.CurrentPriorityOrder);

            _formatMap.ClassificationFormatMappingChanged += (sender, args) =>
                {
                    if (!_inUpdate && _view != null && !_view.IsClosed)
                    {
                        _view.VisualElement.Dispatcher.BeginInvoke(new Action(FixFormatMap));
                    }
                };

            _view.VisualElement.Dispatcher.BeginInvoke(new Action(FixFormatMap));
        }

        void FixFormatMap()
        {
            if (_view == null || _view.IsClosed)
                return;

            var bufferAdapter = _adaptersService.GetBufferAdapter(_view.TextBuffer);

            if (bufferAdapter == null)
                return;

            Guid fontCategory = DefGuidList.guidTextEditorFontCategory;
            Guid languageService;
            if (0 != bufferAdapter.GetLanguageServiceID(out languageService))
                return;

            FontsAndColorsCategory category = new FontsAndColorsCategory(languageService, fontCategory, fontCategory);

            var info = _fcService.GetFontAndColorInformation(category);

            if (info == null)
                return;

            // This is pretty dirty. IVsFontsAndColorsInformation doesn't give you a count, and I don't really want
            // to go through the ugly of finding the eventual colorable items provider to ask for its count, so this nasty
            // little loop will go until an index past the count (at which point it returns null).
            HashSet<IClassificationType> types = new HashSet<IClassificationType>(_formatMap.CurrentPriorityOrder);

            for (int i = 1; i < 1000; i++)
            {
                var type = info.GetClassificationType(i);
                if (type == null)
                    break;

                types.Add(type);
            }

            FixFormatMap(types);
        }

        void FixFormatMap(IEnumerable<IClassificationType> classificationTypes)
        {
            try
            {
                _inUpdate = true;

                foreach (var type in classificationTypes)
                {
                    if (type == null)
                        continue;

                    // There are a couple we want to skip, no matter what.  These are classification types that aren't
                    // used for text formatting.
                    string name = type.Classification.ToUpperInvariant();

                    if (name.Contains("WORD WRAP GLYPH") ||
                        name.Contains("LINE NUMBER") ||
                        name == "STRING")
                        continue;

                    var format = _formatMap.GetTextProperties(type);

                    if (format.BackgroundBrushEmpty)
                        continue;

                    var solidColorBrush = format.BackgroundBrush as SolidColorBrush;
                    if (solidColorBrush != null && solidColorBrush.Opacity == BackgroundOpacity)
                    {
                        format = format.SetBackgroundBrush(new SolidColorBrush(solidColorBrush.Color) { Opacity = Transparent });
                        _formatMap.SetTextProperties(type, format);
                    }
                }
            }
            catch (Exception)
            {
                // Do nothing, just prevent this exception from bringing down the editor.
            }
            finally
            {
                _inUpdate = false;
            }
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            foreach (ITextViewLine line in e.NewOrReformattedLines)
            {
                this.CreateVisuals(line);
            }
        }

        private void CreateVisuals(ITextViewLine line)
        {
            foreach (var tagSpan in _aggregator.GetTags(line.ExtentAsMappingSpan))
            {
                foreach (var span in tagSpan.Span.GetSpans(_view.TextSnapshot))
                {
                    var textProperties = _formatMap.GetTextProperties(tagSpan.Tag.ClassificationType);

                    if (textProperties.BackgroundBrushEmpty)
                        continue;

                    var solidColorBrush = textProperties.BackgroundBrush as SolidColorBrush;
                    if (solidColorBrush == null || solidColorBrush.Opacity != Transparent)
                        continue;

                    Brush brush = new SolidColorBrush(solidColorBrush.Color) { Opacity = 1.0 };

                    bool extendToRight = span.Span.End == line.End;

                    CreateAndAddAdornment(line, span, brush, extendToRight);
                }
            }
        }

        void CreateAndAddAdornment(ITextViewLine line, SnapshotSpan span, Brush brush, bool extendToRight)
        {
            var markerGeometry = _view.TextViewLines.GetMarkerGeometry(span);

            double left = markerGeometry.Bounds.Left;
            double width = extendToRight ? _view.ViewportWidth + _view.MaxTextRightCoordinate : markerGeometry.Bounds.Width;

            Rect rect = new Rect(left, line.Top, width, line.Height);

            RectangleGeometry geometry = new RectangleGeometry(rect);

            GeometryDrawing drawing = new GeometryDrawing(brush, new Pen(), geometry);
            drawing.Freeze();

            DrawingImage drawingImage = new DrawingImage(drawing);
            drawingImage.Freeze();

            Image image = new Image();
            image.Source = drawingImage;

            Canvas.SetLeft(image, geometry.Bounds.Left);
            Canvas.SetTop(image, geometry.Bounds.Top);

            _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, null, image, null);
        }
    }
}
