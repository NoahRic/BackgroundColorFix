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

        const bool BETA2 = true;

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
                    if (!_inUpdate)
                    {
                        FixFormatMap(_formatMap.CurrentPriorityOrder);
                    }
                };

            _view.GotAggregateFocus += GotAggregateFocus;
        }

        void GotAggregateFocus(object sender, EventArgs e)
        {
            _view.GotAggregateFocus -= GotAggregateFocus;

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

            // This is *really* dirty.  Why doesn't IVsFontsAndColorsInformation give you a count?!
            List<IClassificationType> types = new List<IClassificationType>();

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

                    // There are a couple we want to skip, for sure
                    string name = type.Classification.ToUpperInvariant();

                    if (name.Contains("WORD WRAP GLYPH") ||
                        name.Contains("LINE NUMBER"))
                        continue;

                    var format = _formatMap.GetTextProperties(type);

                    if (format.BackgroundBrushEmpty)
                        continue;

                    var solidColorBrush = format.BackgroundBrush as SolidColorBrush;
                    if (solidColorBrush != null && solidColorBrush.Opacity == 0.5)
                    {
                        format = format.SetBackgroundBrush(new SolidColorBrush(solidColorBrush.Color) { Opacity = 0.0 });
                        _formatMap.SetTextProperties(type, format);
                    }
                }
            }
            finally
            {
                _inUpdate = false;
            }
        }

        /// <summary>
        /// On layout change add the adornment to any reformatted lines
        /// </summary>
        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            foreach (ITextViewLine line in e.NewOrReformattedLines)
            {
                this.CreateVisuals(line);
            }
        }

        /// <summary>
        /// Within the given line add the scarlet box behind the a
        /// </summary>
        private void CreateVisuals(ITextViewLine line)
        {
            foreach (var tagSpan in _aggregator.GetTags(line.Extent))
            {
                foreach (var span in tagSpan.Span.GetSpans(_view.TextSnapshot))
                {
                    var textProperties = _formatMap.GetTextProperties(tagSpan.Tag.ClassificationType);

                    if (textProperties.BackgroundBrushEmpty)
                        continue;

                    var solidColorBrush = textProperties.BackgroundBrush as SolidColorBrush;
                    if (solidColorBrush == null || solidColorBrush.Opacity != 0.0)
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

            //Align the image with the top of the bounds of the text geometry
            Canvas.SetLeft(image, geometry.Bounds.Left);
            Canvas.SetTop(image, geometry.Bounds.Top);

            _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, null, image, null);
        }
    }
}
