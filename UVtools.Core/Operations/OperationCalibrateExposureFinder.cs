﻿/*
 *                     GNU AFFERO GENERAL PUBLIC LICENSE
 *                       Version 3, 19 November 2007
 *  Copyright (C) 2007 Free Software Foundation, Inc. <https://fsf.org/>
 *  Everyone is permitted to copy and distribute verbatim copies
 *  of this license document, but changing it is not allowed.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using MoreLinq;
using UVtools.Core.Extensions;
using UVtools.Core.FileFormats;
using UVtools.Core.Objects;

namespace UVtools.Core.Operations
{
    [Serializable]
    public sealed class OperationCalibrateExposureFinder : Operation
    {
        #region Subclasses

        public sealed class BullsEyeCircle
        {
            public ushort Diameter { get; set; }
            public ushort Radius => (ushort) (Diameter / 2);
            public ushort Thickness { get; set; } = 10;

            public BullsEyeCircle() {}

            public BullsEyeCircle(ushort diameter, ushort thickness)
            {
                Diameter = diameter;
                Thickness = thickness;
            }
        }
        #endregion

        #region Constants

        const byte TextMarkingSpacing = 60;
        const byte TextMarkingLineBreak = 30;
        const FontFace TextMarkingFontFace = Emgu.CV.CvEnum.FontFace.HersheyDuplex;
        const byte TextMarkingStartX = 10;
        //const byte TextStartY = 50;
        const double TextMarkingScale = 0.8;
        const byte TextMarkingThickness = 2;

        #endregion

        #region Members
        private decimal _displayWidth;
        private decimal _displayHeight;
        private decimal _layerHeight;
        private ushort _bottomLayers;
        private decimal _bottomExposure;
        private decimal _normalExposure;
        private decimal _topBottomMargin = 5;
        private decimal _leftRightMargin = 10;
        private byte _chamferLayers = 0;
        private byte _erodeBottomIterations = 0;
        private decimal _partMargin = 0;
        private bool _enableAntiAliasing = false;
        private bool _mirrorOutput;
        private decimal _baseHeight = 1;
        private decimal _featuresHeight = 1;
        private decimal _featuresMargin = 2m;
        
        private ushort _staircaseThickness = 40;
        
        private bool _holesEnabled = false;
        private CalibrateExposureFinderShapes _holeShape = CalibrateExposureFinderShapes.Square;
        private Measures _unitOfMeasure = Measures.Pixels;
        private string _holeDiametersPx = "2, 3, 4, 5, 6, 7, 8, 9, 10, 11";
        private string _holeDiametersMm = "0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.2";

        private bool _barsEnabled = true;
        private decimal _barSpacing = 1.5m;
        private decimal _barLength = 4;
        private sbyte _barVerticalSplitter = 0;
        private byte _barFenceThickness = 10;
        private sbyte _barFenceOffset = 4;
        private string _barThicknessesPx = "4, 6, 8, 60"; //"4, 6, 8, 10, 12, 14, 16, 18, 20";
        private string _barThicknessesMm = "0.2, 0.3, 0.4, 3"; //"0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1, 1.2";

        private bool _textEnabled = true;
        private FontFace _textFont = TextMarkingFontFace;
        private double _textScale = 1;
        private byte _textThickness = 2;
        private string _text = "ABHJQRWZ%&#"; //"ABGHJKLMQRSTUVWXZ%&#";

        private bool _multipleBrightness;
        private CalibrateExposureFinderMultipleBrightnessExcludeFrom _multipleBrightnessExcludeFrom = CalibrateExposureFinderMultipleBrightnessExcludeFrom.BottomAndBase;
        private string _multipleBrightnessValues = "255, 242, 230, 217, 204, 191";
        private decimal _multipleBrightnessGenExposureTime;


        private bool _multipleLayerHeight;
        private decimal _multipleLayerHeightMaximum = 0.1m;
        private decimal _multipleLayerHeightStep = 0.01m;

        private bool _dontLiftSamePositionedLayers;
        private bool _zeroLightOffSamePositionedLayers;
        private bool _multipleExposures;
        private ExposureGenTypes _exposureGenType = ExposureGenTypes.Linear;
        private bool _exposureGenIgnoreBaseExposure;
        private decimal _exposureGenBottomStep = 0;
        private decimal _exposureGenNormalStep = 0.2m;
        private byte _exposureGenTests = 4;
        private decimal _exposureGenManualLayerHeight;
        private decimal _exposureGenManualBottom;
        private decimal _exposureGenManualNormal;
        private RangeObservableCollection<ExposureItem> _exposureTable = new();

        private bool _bullsEyeEnabled = true;
        private string _bullsEyeConfigurationPx = "26:5, 60:10, 116:15, 190:20";
        private string _bullsEyeConfigurationMm = "1.3:0.25, 3:0.5, 5.8:0.75, 9.5:1";
        private bool _bullsEyeInvertQuadrants = true;

        private bool _counterTrianglesEnabled = true;
        private sbyte _counterTrianglesTipOffset = 3;
        private bool _counterTrianglesFence = false;

        private bool _patternModel;
        private byte _bullsEyeFenceThickness = 10;
        private sbyte _bullsEyeFenceOffset;
        private bool _patternModelGlueBottomLayers = true;

        #endregion

        #region Overrides

        public override bool CanROI => false;

        public override Enumerations.LayerRangeSelection StartLayerRangeSelection => Enumerations.LayerRangeSelection.None;

        public override string Title => "Exposure time finder";
        public override string Description =>
            "Generates test models with various strategies and increments to verify the best exposure time for a given layer height.\n" +
            "You must repeat this test when change any of the following: printer, LEDs, resin and exposure times.\n" +
            "Note: The current opened file will be overwritten with this test, use a dummy or a not needed file.";

        public override string ConfirmationText =>
            $"generate the exposure time finder test?";

        public override string ProgressTitle =>
            $"Generating the exposure time finder test";

        public override string ProgressAction => "Generated layers";

        public override string ValidateInternally()
        {
            var sb = new StringBuilder();

            if (_displayWidth <= 0)
            {
                sb.AppendLine("Display width must be a positive value.");
            }

            if (_displayHeight <= 0)
            {
                sb.AppendLine("Display height must be a positive value.");
            }

            if (_chamferLayers * _layerHeight > _baseHeight)
            {
                sb.AppendLine("The chamfer can't be higher than the base height, lower the chamfer layer count.");
            }

            if (_multipleExposures)
            {
                var endLayerHeight = _multipleLayerHeight ? _multipleLayerHeightMaximum : _layerHeight;
                for (decimal layerHeight = _layerHeight;
                    layerHeight <= endLayerHeight;
                    layerHeight += _multipleLayerHeightStep)
                {
                    bool found = false;
                    foreach (var exposureItem in _exposureTable)
                    {
                        if (exposureItem.LayerHeight == layerHeight && exposureItem.IsValid)
                        {
                            found = true;
                            break;
                        }
                    }
                    if(!found)
                        sb.AppendLine($"[ME]: The {Layer.ShowHeight(layerHeight)}mm layer height have no set exposure(s).");
                }
            }

            if (_multipleBrightness)
            {
                if (MultipleBrightnessValuesArray.Length == 0)
                {
                    sb.AppendLine($"Multiple brightness tests are enabled but no valid values are set, use from 1 to 255.");
                }
            }

            if (_patternModel)
            {
                if (!CanPatternModel)
                {
                    sb.AppendLine($"Unable to pattern the loaded model within the available space.");
                }

                if (!_multipleBrightness && !_multipleExposures)
                {
                    sb.AppendLine($"Pattern the loaded model requires either multiple brightness or multiple exposures to use with.");
                }
            }
            else
            {
                if (Bars.Length <= 0 && Holes.Length <= 0 && BullsEyes.Length <= 0 && TextSize.IsEmpty)
                {
                    sb.AppendLine("No objects to output, enable at least 1 feature.");
                }
            }

            return sb.ToString();
        }

        public override string ToString()
        {
            var result = $"[Layer Height: {_layerHeight}] " +
                         $"[Bottom layers: {_bottomLayers}] " +
                         $"[Exposure: {_bottomExposure}/{_normalExposure}] " +
                         $"[TB:{_topBottomMargin} LR:{_leftRightMargin} PM:{_partMargin} FM:{_featuresMargin}]  " +
                         $"[Chamfer: {_chamferLayers}] [Erode: {_erodeBottomIterations}] " +
                         $"[Obj height: {_featuresHeight}] " +
                         $"[Holes: {Holes.Length}] [Bars: {Bars.Length}] [BE: {BullsEyes.Length}] [Text: {!string.IsNullOrWhiteSpace(_text)}]" +
                         $"[AA: {_enableAntiAliasing}] [Mirror: {_mirrorOutput}]";
            if (!string.IsNullOrEmpty(ProfileName)) result = $"{ProfileName}: {result}";
            return result;
        }

        #endregion

        #region Properties

        public decimal DisplayWidth
        {
            get => _displayWidth;
            set
            {
                if(!RaiseAndSetIfChanged(ref _displayWidth, Math.Round(value, 2))) return;
                RaisePropertyChanged(nameof(Xppmm));
            }
        }

        public decimal DisplayHeight
        {
            get => _displayHeight;
            set
            {
                if(!RaiseAndSetIfChanged(ref _displayHeight, Math.Round(value, 2))) return;
                RaisePropertyChanged(nameof(Yppmm));
            }
        }

        public decimal Xppmm => DisplayWidth > 0 ? Math.Round(SlicerFile.Resolution.Width / DisplayWidth, 2) : 0;
        public decimal Yppmm => DisplayHeight > 0 ? Math.Round(SlicerFile.Resolution.Height / DisplayHeight, 2) : 0;
        public decimal Ppmm => Math.Max(Xppmm, Yppmm);

        public decimal LayerHeight
        {
            get => _layerHeight;
            set
            {
                if(!RaiseAndSetIfChanged(ref _layerHeight, Layer.RoundHeight(value))) return;
                RaisePropertyChanged(nameof(BottomLayersMM));
                RaisePropertyChanged(nameof(AvailableLayerHeights));
            }
        }

        public ushort Microns => (ushort)(LayerHeight * 1000);

        public ushort BottomLayers
        {
            get => _bottomLayers;
            set
            {
                if(!RaiseAndSetIfChanged(ref _bottomLayers, value)) return;
                RaisePropertyChanged(nameof(BottomLayersMM));
            }
        }

        public decimal BottomLayersMM => Layer.RoundHeight(LayerHeight * BottomLayers);

        public decimal BottomExposure
        {
            get => _bottomExposure;
            set
            {
                if(!RaiseAndSetIfChanged(ref _bottomExposure, Math.Round(value, 2))) return;
                RaisePropertyChanged(nameof(MultipleBrightnessTable));
            }
        }

        public decimal NormalExposure
        {
            get => _normalExposure;
            set
            {
                if(!RaiseAndSetIfChanged(ref _normalExposure, Math.Round(value, 2))) return;
                RaisePropertyChanged(nameof(MultipleBrightnessTable));
            }
        }

        public decimal TopBottomMargin
        {
            get => _topBottomMargin;
            set => RaiseAndSetIfChanged(ref _topBottomMargin, Math.Round(value, 2));
        }

        public decimal LeftRightMargin
        {
            get => _leftRightMargin;
            set => RaiseAndSetIfChanged(ref _leftRightMargin, Math.Round(value, 2));
        }

        public byte ChamferLayers
        {
            get => _chamferLayers;
            set => RaiseAndSetIfChanged(ref _chamferLayers, value);
        }

        public byte ErodeBottomIterations
        {
            get => _erodeBottomIterations;
            set => RaiseAndSetIfChanged(ref _erodeBottomIterations, value);
        }

        public decimal PartMargin
        {
            get => _partMargin;
            set => RaiseAndSetIfChanged(ref _partMargin, Math.Round(value, 2));
        }

        public bool EnableAntiAliasing
        {
            get => _enableAntiAliasing;
            set => RaiseAndSetIfChanged(ref _enableAntiAliasing, value);
        }

        public bool MirrorOutput
        {
            get => _mirrorOutput;
            set => RaiseAndSetIfChanged(ref _mirrorOutput, value);
        }

        public decimal BaseHeight
        {
            get => _baseHeight;
            set => RaiseAndSetIfChanged(ref _baseHeight, Math.Round(value, 2));
        }

        public decimal FeaturesHeight
        {
            get => _featuresHeight;
            set => RaiseAndSetIfChanged(ref _featuresHeight, Math.Round(value, 2));
        }

        public decimal TotalHeight => _baseHeight + _featuresHeight;
        
        public decimal FeaturesMargin
        {
            get => _featuresMargin;
            set => RaiseAndSetIfChanged(ref _featuresMargin, Math.Round(value, 2));
        }

        public ushort StaircaseThickness
        {
            get => _staircaseThickness;
            set => RaiseAndSetIfChanged(ref _staircaseThickness, value);
        }

        public bool CounterTrianglesEnabled
        {
            get => _counterTrianglesEnabled;
            set => RaiseAndSetIfChanged(ref _counterTrianglesEnabled, value);
        }

        public sbyte CounterTrianglesTipOffset
        {
            get => _counterTrianglesTipOffset;
            set => RaiseAndSetIfChanged(ref _counterTrianglesTipOffset, value);
        }

        public bool CounterTrianglesFence
        {
            get => _counterTrianglesFence;
            set => RaiseAndSetIfChanged(ref _counterTrianglesFence, value);
        }

        public bool HolesEnabled
        {
            get => _holesEnabled;
            set => RaiseAndSetIfChanged(ref _holesEnabled, value);
        }

        public CalibrateExposureFinderShapes HoleShape
        {
            get => _holeShape;
            set => RaiseAndSetIfChanged(ref _holeShape, value);
        }

        public Measures UnitOfMeasure
        {
            get => _unitOfMeasure;
            set
            {
                if (!RaiseAndSetIfChanged(ref _unitOfMeasure, value)) return;
                RaisePropertyChanged(nameof(IsUnitOfMeasureMm));
            }
        }

        public bool IsUnitOfMeasureMm => _unitOfMeasure == Measures.Millimeters;

        public string HoleDiametersMm
        {
            get => _holeDiametersMm;
            set => RaiseAndSetIfChanged(ref _holeDiametersMm, value);
        }

        public string HoleDiametersPx
        {
            get => _holeDiametersPx;
            set => RaiseAndSetIfChanged(ref _holeDiametersPx, value);
        }

        /// <summary>
        /// Gets all holes in pixels and ordered
        /// </summary>
        public int[] Holes
        {
            get
            {
                if (!_holesEnabled)
                {
                    return Array.Empty<int>();
                }

                List<int> holes = new();

                if (_unitOfMeasure == Measures.Millimeters)
                {
                    var split = _holeDiametersMm.Split(',', StringSplitOptions.TrimEntries);
                    foreach (var mmStr in split)
                    {
                        if (string.IsNullOrWhiteSpace(mmStr)) continue;
                        if (!decimal.TryParse(mmStr, out var mm)) continue;
                        var mmPx = (int)Math.Floor(mm * Ppmm);
                        if (mmPx is <= 0 or > 500) continue;
                        if(holes.Contains(mmPx)) continue;
                        holes.Add(mmPx);
                    }
                }
                else
                {
                    var split = _holeDiametersPx.Split(',', StringSplitOptions.TrimEntries);
                    foreach (var pxStr in split)
                    {
                        if (string.IsNullOrWhiteSpace(pxStr)) continue;
                        if (!int.TryParse(pxStr, out var px)) continue;
                        if (px is <= 0 or > 500) continue;
                        if (holes.Contains(px)) continue;
                        holes.Add(px);
                    }
                }

                return holes.OrderBy(pixels => pixels).ToArray();
            }
        }

        public int GetHolesHeight(int[] holes)
        {
            if (holes.Length == 0) return 0;
            return (int) (holes.Sum() + (holes.Length-1) * _featuresMargin * Yppmm);
        }

        public bool BarsEnabled
        {
            get => _barsEnabled;
            set => RaiseAndSetIfChanged(ref _barsEnabled, value);
        }

        public decimal BarSpacing
        {
            get => _barSpacing;
            set => RaiseAndSetIfChanged(ref _barSpacing, value);
        }

        public decimal BarLength
        {
            get => _barLength;
            set => RaiseAndSetIfChanged(ref _barLength, value);
        }

        public sbyte BarVerticalSplitter
        {
            get => _barVerticalSplitter;
            set => RaiseAndSetIfChanged(ref _barVerticalSplitter, value);
        }

        public byte BarFenceThickness
        {
            get => _barFenceThickness;
            set => RaiseAndSetIfChanged(ref _barFenceThickness, value);
        }

        public sbyte BarFenceOffset
        {
            get => _barFenceOffset;
            set => RaiseAndSetIfChanged(ref _barFenceOffset, value);
        }

        public string BarThicknessesPx
        {
            get => _barThicknessesPx;
            set => RaiseAndSetIfChanged(ref _barThicknessesPx, value);
        }

        public string BarThicknessesMm
        {
            get => _barThicknessesMm;
            set => RaiseAndSetIfChanged(ref _barThicknessesMm, value);
        }

        /// <summary>
        /// Gets all holes in pixels and ordered
        /// </summary>
        public int[] Bars
        {
            get
            {
                if (!_barsEnabled)
                {
                    return Array.Empty<int>();
                }

                List<int> bars = new();

                if (_unitOfMeasure == Measures.Millimeters)
                {
                    var split = _barThicknessesMm.Split(',', StringSplitOptions.TrimEntries);
                    foreach (var mmStr in split)
                    {
                        if (string.IsNullOrWhiteSpace(mmStr)) continue;
                        if (!decimal.TryParse(mmStr, out var mm)) continue;
                        var mmPx = (int)Math.Floor(mm * Xppmm);
                        if (mmPx is <= 0 or > 500) continue;
                        if (bars.Contains(mmPx)) continue;
                        bars.Add(mmPx);
                    }
                }
                else
                {
                    var split = _barThicknessesPx.Split(',', StringSplitOptions.TrimEntries);
                    foreach (var pxStr in split)
                    {
                        if (string.IsNullOrWhiteSpace(pxStr)) continue;
                        if (!int.TryParse(pxStr, out var px)) continue;
                        if (px is <= 0 or > 500) continue;
                        if (bars.Contains(px)) continue;
                        bars.Add(px);
                    }
                }

                return bars.OrderBy(pixels => pixels).ToArray();
            }
        }

        public int GetBarsLength(int[] bars)
        {
            if (bars.Length == 0) return 0;
            int len = (int) (bars.Sum() + (bars.Length + 1) * _barSpacing * Yppmm);
            if (_barFenceThickness > 0)
            {
                len = Math.Max(len, len + _barFenceThickness * 2 + _barFenceOffset * 2);
            }
            return len;
        }

        public bool TextEnabled
        {
            get => _textEnabled;
            set => RaiseAndSetIfChanged(ref _textEnabled, value);
        }

        public static Array TextFonts => Enum.GetValues(typeof(FontFace));

        public FontFace TextFont
        {
            get => _textFont;
            set => RaiseAndSetIfChanged(ref _textFont, value);
        }

        public double TextScale
        {
            get => _textScale;
            set => RaiseAndSetIfChanged(ref _textScale, Math.Round(value, 2));
        }

        public byte TextThickness
        {
            get => _textThickness;
            set => RaiseAndSetIfChanged(ref _textThickness, value);
        }

        public string Text
        {
            get => _text;
            set => RaiseAndSetIfChanged(ref _text, value);
        }

        public Size TextSize
        {
            get
            {
                if (!_textEnabled || string.IsNullOrWhiteSpace(_text)) return Size.Empty;
                int baseline = 0;
                return CvInvoke.GetTextSize(_text, _textFont, _textScale, _textThickness, ref baseline);
            }
        }

        public bool MultipleBrightness
        {
            get => _multipleBrightness;
            set => RaiseAndSetIfChanged(ref _multipleBrightness, value);
        }

        public CalibrateExposureFinderMultipleBrightnessExcludeFrom MultipleBrightnessExcludeFrom
        {
            get => _multipleBrightnessExcludeFrom;
            set => RaiseAndSetIfChanged(ref _multipleBrightnessExcludeFrom, value);
        }

        public string MultipleBrightnessValues
        {
            get => _multipleBrightnessValues;
            set
            {
                if(!RaiseAndSetIfChanged(ref _multipleBrightnessValues, value)) return;
                RaisePropertyChanged(nameof(MultipleBrightnessTable));
            }
        }

        public List<ExposureItem> MultipleBrightnessTable
        {
            get
            {
                var brightnesses = MultipleBrightnessValuesArray;
                return brightnesses.Select(brightness => (ExposureItem) 
                    new(
                        _layerHeight,
                        Math.Round(brightness * _bottomExposure / byte.MaxValue, 2),
                        Math.Round(brightness * _normalExposure / byte.MaxValue, 2),
                        brightness)).ToList();
            }
        }

        public decimal MultipleBrightnessGenExposureTime
        {
            get => _multipleBrightnessGenExposureTime;
            set => RaiseAndSetIfChanged(ref _multipleBrightnessGenExposureTime, value);
        }

        /// <summary>
        /// Gets all holes in pixels and ordered
        /// </summary>
        public byte[] MultipleBrightnessValuesArray
        {
            get
            {
                List<byte> values = new();

                var split = _multipleBrightnessValues.Split(',', StringSplitOptions.TrimEntries);
                foreach (var brightnessStr in split)
                {
                    if (string.IsNullOrWhiteSpace(brightnessStr)) continue;
                    if (!byte.TryParse(brightnessStr, out var brightness)) continue;
                    if (brightness <= 0 || brightness > 255) continue;
                    if (values.Contains(brightness)) continue;
                    values.Add(brightness);
                }

                return values.OrderByDescending(brightness => brightness).ToArray();
            }
        }


        public bool MultipleLayerHeight
        {
            get => _multipleLayerHeight;
            set
            {
                if(!RaiseAndSetIfChanged(ref _multipleLayerHeight, value)) return;
                RaisePropertyChanged(nameof(AvailableLayerHeights));
            }
        }

        public decimal MultipleLayerHeightMaximum
        {
            get => _multipleLayerHeightMaximum;
            set
            {
                if(!RaiseAndSetIfChanged(ref _multipleLayerHeightMaximum, value)) return;
                RaisePropertyChanged(nameof(AvailableLayerHeights));
            }
        }

        public decimal MultipleLayerHeightStep
        {
            get => _multipleLayerHeightStep;
            set
            {
                if(!RaiseAndSetIfChanged(ref _multipleLayerHeightStep, value)) return;
                RaisePropertyChanged(nameof(AvailableLayerHeights));
            }
        }

        public bool DontLiftSamePositionedLayers
        {
            get => _dontLiftSamePositionedLayers;
            set => RaiseAndSetIfChanged(ref _dontLiftSamePositionedLayers, value);
        }

        public bool ZeroLightOffSamePositionedLayers
        {
            get => _zeroLightOffSamePositionedLayers;
            set => RaiseAndSetIfChanged(ref _zeroLightOffSamePositionedLayers, value);
        }

        public bool MultipleExposures
        {
            get => _multipleExposures;
            set => RaiseAndSetIfChanged(ref _multipleExposures, value);
        }

        public ExposureGenTypes ExposureGenType
        {
            get => _exposureGenType;
            set => RaiseAndSetIfChanged(ref _exposureGenType, value);
        }

        public bool ExposureGenIgnoreBaseExposure
        {
            get => _exposureGenIgnoreBaseExposure;
            set => RaiseAndSetIfChanged(ref _exposureGenIgnoreBaseExposure, value);
        }

        public decimal ExposureGenBottomStep
        {
            get => _exposureGenBottomStep;
            set => RaiseAndSetIfChanged(ref _exposureGenBottomStep, Math.Round(value, 2));
        }

        public decimal ExposureGenNormalStep
        {
            get => _exposureGenNormalStep;
            set => RaiseAndSetIfChanged(ref _exposureGenNormalStep, Math.Round(value, 2));
        }

        public byte ExposureGenTests
        {
            get => _exposureGenTests;
            set => RaiseAndSetIfChanged(ref _exposureGenTests, value);
        }

        public decimal ExposureGenManualLayerHeight
        {
            get => _exposureGenManualLayerHeight;
            set => RaiseAndSetIfChanged(ref _exposureGenManualLayerHeight, value);
        }

        public decimal[] AvailableLayerHeights
        {
            get
            {
                List<decimal> layerHeights = new();
                var endLayerHeight = _multipleLayerHeight ? _multipleLayerHeightMaximum : _layerHeight;
                for (decimal layerHeight = _layerHeight; layerHeight <= endLayerHeight; layerHeight += _multipleLayerHeightStep)
                {
                    layerHeights.Add(Layer.RoundHeight(layerHeight));
                }

                return layerHeights.ToArray();
            }
        }

        public decimal ExposureGenManualBottom
        {
            get => _exposureGenManualBottom;
            set => RaiseAndSetIfChanged(ref _exposureGenManualBottom, value);
        }

        public decimal ExposureGenManualNormal
        {
            get => _exposureGenManualNormal;
            set => RaiseAndSetIfChanged(ref _exposureGenManualNormal, value);
        }

        public ExposureItem ExposureManualEntry => new (_exposureGenManualLayerHeight, _exposureGenManualBottom, _exposureGenManualNormal);


        public RangeObservableCollection<ExposureItem> ExposureTable
        {
            get => _exposureTable;
            set => RaiseAndSetIfChanged(ref _exposureTable, value);
        }

        public bool BullsEyeEnabled
        {
            get => _bullsEyeEnabled;
            set => RaiseAndSetIfChanged(ref _bullsEyeEnabled, value);
        }

        public string BullsEyeConfigurationPx
        {
            get => _bullsEyeConfigurationPx;
            set => RaiseAndSetIfChanged(ref _bullsEyeConfigurationPx, value);
        }

        public string BullsEyeConfigurationMm
        {
            get => _bullsEyeConfigurationMm;
            set => RaiseAndSetIfChanged(ref _bullsEyeConfigurationMm, value);
        }

        public byte BullsEyeFenceThickness
        {
            get => _bullsEyeFenceThickness;
            set => RaiseAndSetIfChanged(ref _bullsEyeFenceThickness, value);
        }

        public sbyte BullsEyeFenceOffset
        {
            get => _bullsEyeFenceOffset;
            set => RaiseAndSetIfChanged(ref _bullsEyeFenceOffset, value);
        }

        public bool BullsEyeInvertQuadrants
        {
            get => _bullsEyeInvertQuadrants;
            set => RaiseAndSetIfChanged(ref _bullsEyeInvertQuadrants, value);
        }

        /// <summary>
        /// Gets all holes in pixels and ordered
        /// </summary>
        public BullsEyeCircle[] BullsEyes
        {
            get
            {
                if (!_bullsEyeEnabled)
                {
                    return Array.Empty<BullsEyeCircle>();
                }

                List<BullsEyeCircle> bulleyes = new();
                
                if (_unitOfMeasure == Measures.Millimeters)
                {
                    var splitGroup = _bullsEyeConfigurationMm.Split(',', StringSplitOptions.TrimEntries);
                    foreach (var group in splitGroup)
                    {
                        var splitDiameterThickness = group.Split(':', StringSplitOptions.TrimEntries);
                        if (splitDiameterThickness.Length < 2) continue;

                        if (string.IsNullOrWhiteSpace(splitDiameterThickness[0]) ||
                            string.IsNullOrWhiteSpace(splitDiameterThickness[1])) continue;
                        if (!decimal.TryParse(splitDiameterThickness[0], out var diameterMm)) continue;
                        if (!decimal.TryParse(splitDiameterThickness[1], out var thicknessMm)) continue;
                        var diameter = (int)Math.Floor(diameterMm * Ppmm);
                        if (diameterMm is <= 0 or > 500) continue;
                        var thickness = (int)Math.Floor(thicknessMm * Ppmm);
                        if (thickness is <= 0 or > 500) continue;
                        if (bulleyes.Exists(circle => circle.Diameter == diameter)) continue;
                        bulleyes.Add(new BullsEyeCircle((ushort)diameter, (ushort)thickness));
                    }
                }
                else
                {
                    var splitGroup = _bullsEyeConfigurationPx.Split(',', StringSplitOptions.TrimEntries);
                    foreach (var group in splitGroup)
                    {
                        var splitDiameterThickness = group.Split(':', StringSplitOptions.TrimEntries);
                        if (splitDiameterThickness.Length < 2) continue;

                        if (string.IsNullOrWhiteSpace(splitDiameterThickness[0]) ||
                            string.IsNullOrWhiteSpace(splitDiameterThickness[1])) continue;
                        if (!int.TryParse(splitDiameterThickness[0], out var diameter)) continue;
                        if (!int.TryParse(splitDiameterThickness[1], out var thickness)) continue;
                        if (diameter is <= 0 or > 500) continue;
                        if (thickness is <= 0 or > 500) continue;
                        if (bulleyes.Exists(circle => circle.Diameter == diameter)) continue;
                        bulleyes.Add(new BullsEyeCircle((ushort) diameter, (ushort) thickness));
                    }
                }
                
                return bulleyes.OrderBy(circle => circle.Diameter).DistinctBy(circle => circle.Diameter).ToArray();
            }
        }
        public int GetBullsEyeMaxPanelDiameter(BullsEyeCircle[] bullseyes)
        {
            if (!_bullsEyeEnabled || bullseyes.Length == 0) return 0;
            var diameter = GetBullsEyeMaxDiameter(bullseyes);
            return Math.Max(diameter, diameter + _bullsEyeFenceThickness + _bullsEyeFenceOffset * 2);
        }

        public int GetBullsEyeMaxDiameter(BullsEyeCircle[] bullseyes)
        {
            if (!_bullsEyeEnabled || bullseyes.Length == 0) return 0;
            return bullseyes[^1].Diameter + bullseyes[^1].Thickness / 2;
        }

        public bool PatternModel
        {
            get => _patternModel;
            set
            {
                if(!RaiseAndSetIfChanged(ref _patternModel, value)) return;
                if (_patternModel)
                {
                    LayerHeight = (decimal) SlicerFile.LayerHeight;
                    MultipleLayerHeight = false;
                }
            }
        }

        public bool PatternModelGlueBottomLayers
        {
            get => _patternModelGlueBottomLayers;
            set => RaiseAndSetIfChanged(ref _patternModelGlueBottomLayers, value);
        }


        public bool CanPatternModel => SlicerFile.BoundingRectangle.Width * 2 + _leftRightMargin * 2 + _partMargin * Xppmm < SlicerFile.ResolutionX ||
                                       SlicerFile.BoundingRectangle.Height * 2 + _topBottomMargin * 2 + _partMargin * Yppmm < SlicerFile.ResolutionY;

        #endregion

        #region Constructor

        public OperationCalibrateExposureFinder() { }

        public OperationCalibrateExposureFinder(FileFormat slicerFile) : base(slicerFile)
        { }

        public override void InitWithSlicerFile()
        {
            base.InitWithSlicerFile();
           
            _mirrorOutput = SlicerFile.MirrorDisplay;

            if (SlicerFile.DisplayWidth > 0)
                DisplayWidth = (decimal)SlicerFile.DisplayWidth;
            if (SlicerFile.DisplayHeight > 0)
                DisplayHeight = (decimal)SlicerFile.DisplayHeight;

            if(_layerHeight <= 0) _layerHeight = (decimal)SlicerFile.LayerHeight;
            if(_bottomLayers <= 0) _bottomLayers = SlicerFile.BottomLayerCount;
            if(_bottomExposure <= 0) _bottomExposure = (decimal)SlicerFile.BottomExposureTime;
            if(_normalExposure <= 0) _normalExposure = (decimal)SlicerFile.ExposureTime;

            if (_exposureGenManualBottom == 0)
                _exposureGenManualBottom = (decimal) SlicerFile.BottomExposureTime;
            if (_exposureGenManualNormal == 0)
                _exposureGenManualNormal = (decimal)SlicerFile.ExposureTime;
            if (_multipleBrightnessGenExposureTime == 0)
                _multipleBrightnessGenExposureTime = (decimal)SlicerFile.ExposureTime;

            if (!SlicerFile.HavePrintParameterPerLayerModifier(FileFormat.PrintParameterModifier.ExposureSeconds))
            {
                _multipleLayerHeight = false;
                _multipleExposures = false;
            }
        }

        #endregion

        #region Enums

        public enum CalibrateExposureFinderShapes : byte
        {
            Square,
            Circle
        }
        public static Array ShapesItems => Enum.GetValues(typeof(CalibrateExposureFinderShapes));

        public enum Measures : byte
        {
            Pixels,
            Millimeters,
        }

        public static Array MeasuresItems => Enum.GetValues(typeof(Measures));

        public enum CalibrateExposureFinderMultipleBrightnessExcludeFrom : byte
        {
            None,
            Bottom,
            BottomAndBase
        }
        public static Array MultipleBrightnessExcludeFromItems => Enum.GetValues(typeof(CalibrateExposureFinderMultipleBrightnessExcludeFrom));

        public enum ExposureGenTypes : byte
        {
            Linear,
            Multiplier
        }

        public static Array ExposureGenTypeItems => Enum.GetValues(typeof(ExposureGenTypes));
        #endregion

        #region Equality

        private bool Equals(OperationCalibrateExposureFinder other)
        {
            return _displayWidth == other._displayWidth && _displayHeight == other._displayHeight && _layerHeight == other._layerHeight && _bottomLayers == other._bottomLayers && _bottomExposure == other._bottomExposure && _normalExposure == other._normalExposure && _topBottomMargin == other._topBottomMargin && _leftRightMargin == other._leftRightMargin && _chamferLayers == other._chamferLayers && _erodeBottomIterations == other._erodeBottomIterations && _partMargin == other._partMargin && _enableAntiAliasing == other._enableAntiAliasing && _mirrorOutput == other._mirrorOutput && _baseHeight == other._baseHeight && _featuresHeight == other._featuresHeight && _featuresMargin == other._featuresMargin && _staircaseThickness == other._staircaseThickness && _holesEnabled == other._holesEnabled && _holeShape == other._holeShape && _unitOfMeasure == other._unitOfMeasure && _holeDiametersPx == other._holeDiametersPx && _holeDiametersMm == other._holeDiametersMm && _barsEnabled == other._barsEnabled && _barSpacing == other._barSpacing && _barLength == other._barLength && _barVerticalSplitter == other._barVerticalSplitter && _barFenceThickness == other._barFenceThickness && _barFenceOffset == other._barFenceOffset && _barThicknessesPx == other._barThicknessesPx && _barThicknessesMm == other._barThicknessesMm && _textEnabled == other._textEnabled && _textFont == other._textFont && _textScale.Equals(other._textScale) && _textThickness == other._textThickness && _text == other._text && _multipleBrightness == other._multipleBrightness && _multipleBrightnessExcludeFrom == other._multipleBrightnessExcludeFrom && _multipleBrightnessValues == other._multipleBrightnessValues && _multipleBrightnessGenExposureTime == other._multipleBrightnessGenExposureTime && _multipleLayerHeight == other._multipleLayerHeight && _multipleLayerHeightMaximum == other._multipleLayerHeightMaximum && _multipleLayerHeightStep == other._multipleLayerHeightStep && _dontLiftSamePositionedLayers == other._dontLiftSamePositionedLayers && _zeroLightOffSamePositionedLayers == other._zeroLightOffSamePositionedLayers && _multipleExposures == other._multipleExposures && _exposureGenType == other._exposureGenType && _exposureGenIgnoreBaseExposure == other._exposureGenIgnoreBaseExposure && _exposureGenBottomStep == other._exposureGenBottomStep && _exposureGenNormalStep == other._exposureGenNormalStep && _exposureGenTests == other._exposureGenTests && _exposureGenManualLayerHeight == other._exposureGenManualLayerHeight && _exposureGenManualBottom == other._exposureGenManualBottom && _exposureGenManualNormal == other._exposureGenManualNormal && Equals(_exposureTable, other._exposureTable) && _bullsEyeEnabled == other._bullsEyeEnabled && _bullsEyeConfigurationPx == other._bullsEyeConfigurationPx && _bullsEyeConfigurationMm == other._bullsEyeConfigurationMm && _bullsEyeInvertQuadrants == other._bullsEyeInvertQuadrants && _counterTrianglesEnabled == other._counterTrianglesEnabled && _counterTrianglesTipOffset == other._counterTrianglesTipOffset && _counterTrianglesFence == other._counterTrianglesFence && _patternModel == other._patternModel && _bullsEyeFenceThickness == other._bullsEyeFenceThickness && _bullsEyeFenceOffset == other._bullsEyeFenceOffset && _patternModelGlueBottomLayers == other._patternModelGlueBottomLayers;
        }

        public override bool Equals(object obj)
        {
            return ReferenceEquals(this, obj) || obj is OperationCalibrateExposureFinder other && Equals(other);
        }

        public override int GetHashCode()
        {
            var hashCode = new HashCode();
            hashCode.Add(_displayWidth);
            hashCode.Add(_displayHeight);
            hashCode.Add(_layerHeight);
            hashCode.Add(_bottomLayers);
            hashCode.Add(_bottomExposure);
            hashCode.Add(_normalExposure);
            hashCode.Add(_topBottomMargin);
            hashCode.Add(_leftRightMargin);
            hashCode.Add(_chamferLayers);
            hashCode.Add(_erodeBottomIterations);
            hashCode.Add(_partMargin);
            hashCode.Add(_enableAntiAliasing);
            hashCode.Add(_mirrorOutput);
            hashCode.Add(_baseHeight);
            hashCode.Add(_featuresHeight);
            hashCode.Add(_featuresMargin);
            hashCode.Add(_staircaseThickness);
            hashCode.Add(_holesEnabled);
            hashCode.Add((int) _holeShape);
            hashCode.Add((int) _unitOfMeasure);
            hashCode.Add(_holeDiametersPx);
            hashCode.Add(_holeDiametersMm);
            hashCode.Add(_barsEnabled);
            hashCode.Add(_barSpacing);
            hashCode.Add(_barLength);
            hashCode.Add(_barVerticalSplitter);
            hashCode.Add(_barFenceThickness);
            hashCode.Add(_barFenceOffset);
            hashCode.Add(_barThicknessesPx);
            hashCode.Add(_barThicknessesMm);
            hashCode.Add(_textEnabled);
            hashCode.Add((int) _textFont);
            hashCode.Add(_textScale);
            hashCode.Add(_textThickness);
            hashCode.Add(_text);
            hashCode.Add(_multipleBrightness);
            hashCode.Add((int) _multipleBrightnessExcludeFrom);
            hashCode.Add(_multipleBrightnessValues);
            hashCode.Add(_multipleBrightnessGenExposureTime);
            hashCode.Add(_multipleLayerHeight);
            hashCode.Add(_multipleLayerHeightMaximum);
            hashCode.Add(_multipleLayerHeightStep);
            hashCode.Add(_dontLiftSamePositionedLayers);
            hashCode.Add(_zeroLightOffSamePositionedLayers);
            hashCode.Add(_multipleExposures);
            hashCode.Add((int) _exposureGenType);
            hashCode.Add(_exposureGenIgnoreBaseExposure);
            hashCode.Add(_exposureGenBottomStep);
            hashCode.Add(_exposureGenNormalStep);
            hashCode.Add(_exposureGenTests);
            hashCode.Add(_exposureGenManualLayerHeight);
            hashCode.Add(_exposureGenManualBottom);
            hashCode.Add(_exposureGenManualNormal);
            hashCode.Add(_exposureTable);
            hashCode.Add(_bullsEyeEnabled);
            hashCode.Add(_bullsEyeConfigurationPx);
            hashCode.Add(_bullsEyeConfigurationMm);
            hashCode.Add(_bullsEyeInvertQuadrants);
            hashCode.Add(_counterTrianglesEnabled);
            hashCode.Add(_counterTrianglesTipOffset);
            hashCode.Add(_counterTrianglesFence);
            hashCode.Add(_patternModel);
            hashCode.Add(_bullsEyeFenceThickness);
            hashCode.Add(_bullsEyeFenceOffset);
            hashCode.Add(_patternModelGlueBottomLayers);
            return hashCode.ToHashCode();
        }

        #endregion

        #region Methods

        public void SortExposureTable()
        {
            _exposureTable.Sort();
        }

        public void SanitizeExposureTable()
        {
            _exposureTable.ReplaceCollection(GetSanitizedExposureTable());
        }

        public List<ExposureItem> GetSanitizedExposureTable()
        {
            var list = _exposureTable.ToList().Distinct().ToList();
            list.Sort();
            return list;
        }

        public void GenerateExposure()
        {
            var endLayerHeight = _multipleLayerHeight ? _multipleLayerHeightMaximum : _layerHeight;
            List<ExposureItem> list = new();
            for (decimal layerHeight = _layerHeight;
                layerHeight <= endLayerHeight;
                layerHeight += _multipleLayerHeightStep)
            {
                if(!_exposureGenIgnoreBaseExposure)
                    list.Add(new ExposureItem(layerHeight, _bottomExposure, _normalExposure));
                for (ushort testN = 1; testN <= _exposureGenTests; testN++)
                {
                    decimal bottomExposureTime = 0;
                    decimal exposureTime = 0;

                    switch (_exposureGenType)
                    {
                        case ExposureGenTypes.Linear:
                            bottomExposureTime = _bottomExposure + _exposureGenBottomStep * testN; 
                            exposureTime = _normalExposure + _exposureGenNormalStep * testN; 
                            break;
                        case ExposureGenTypes.Multiplier:
                            bottomExposureTime = _bottomExposure + _bottomExposure * layerHeight * _exposureGenBottomStep * testN;
                            exposureTime = _normalExposure + _normalExposure * layerHeight * _exposureGenNormalStep * testN;
                            break;
                    }

                    ExposureItem item = new(layerHeight, bottomExposureTime, exposureTime);
                    if(list.Contains(item)) continue; // Already on list, skip
                    list.Add(item);
                }
            }

            ExposureTable = new(list);
        }

        public Mat[] GetLayers(bool isPreview = false)
        {
            var holes = Holes;
            var bars = Bars;
            var bulleyes = BullsEyes;
            var textSize = TextSize;

            int featuresMarginX = (int)(Xppmm * _featuresMargin);
            int featuresMarginY = (int)(Yppmm * _featuresMargin);

            int holePanelWidth = holes.Length > 0 ? featuresMarginX * 2 + holes[^1] : 0;
            int holePanelHeight = GetHolesHeight(holes);
            int barsPanelHeight = GetBarsLength(bars);
            int bulleyesDiameter = GetBullsEyeMaxDiameter(bulleyes);
            int bulleyesPanelDiameter = GetBullsEyeMaxPanelDiameter(bulleyes);
            int bulleyesRadius = bulleyesDiameter / 2;
            int yLeftMaxSize = _staircaseThickness + featuresMarginY + Math.Max(barsPanelHeight, textSize.Width) + bulleyesPanelDiameter;
            int yRightMaxSize = _staircaseThickness + holePanelHeight + featuresMarginY * 2;
            
            int xSize = featuresMarginX;
            int ySize = TextMarkingSpacing + featuresMarginY;

            if (barsPanelHeight > 0 || textSize.Width > 0)
            {
                yLeftMaxSize += featuresMarginY;
            }

            int barLengthPx = (int) (_barLength * Xppmm);
            int barSpacingPx = (int) (_barSpacing * Yppmm);
            int barsPanelWidth = 0;

            if (bars.Length > 0)
            {
                barsPanelWidth = barLengthPx * 2 + _barVerticalSplitter;
                if (_barFenceThickness > 0)
                {
                    barsPanelWidth = Math.Max(barsPanelWidth, barsPanelWidth + _barFenceThickness * 2 + _barFenceOffset * 2);
                }
                xSize += barsPanelWidth + featuresMarginX;
            }

            if (!textSize.IsEmpty)
            {
                xSize += textSize.Height + featuresMarginX;
            }

            int bullseyeYPos = yLeftMaxSize - bulleyesPanelDiameter / 2;

            if (bulleyes.Length > 0)
            {
                xSize = Math.Max(xSize, bulleyesPanelDiameter + featuresMarginX * 2);
                yLeftMaxSize += featuresMarginY + 24;
            }

            int bullseyeXPos = xSize / 2;
            
            if (holePanelWidth > 0)
            {
                xSize -= featuresMarginX;
            }
            
            xSize += holePanelWidth;
            int negativeSideWidth = xSize;
            xSize += holePanelWidth;

            int positiveSideWidth = xSize - holePanelWidth;

            ySize += Math.Max(yLeftMaxSize, yRightMaxSize+10);

            Rectangle rect = new(new Point(0, 0), new Size(xSize, ySize));
            var layers = new Mat[2];
            layers[0] = EmguExtensions.InitMat(rect.Size);

            CvInvoke.Rectangle(layers[0], rect, EmguExtensions.WhiteColor, -1, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
            layers[1] = layers[0].NewBlank();
            if (holes.Length > 0)
            {
                CvInvoke.Rectangle(layers[1],
                    new Rectangle(rect.Size.Width - holePanelWidth, 0, rect.Size.Width, layers[0].Height),
                    EmguExtensions.WhiteColor, -1, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
            }

            
            int xPos = 0;
            int yPos = 0;

            // Print staircase
            if (isPreview && _staircaseThickness > 0)
            {
                CvInvoke.Rectangle(layers[1],
                    new Rectangle(0, 0, layers[1].Size.Width-holePanelWidth, _staircaseThickness),
                    EmguExtensions.WhiteColor, -1, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
            }

            // Print holes
            for (var layerIndex = 0; layerIndex < layers.Length; layerIndex++)
            {
                var layer = layers[layerIndex];
                yPos = featuresMarginY + _staircaseThickness;
                for (int i = 0; i < holes.Length; i++)
                {
                    var diameter = holes[i];
                    var radius = diameter / 2;
                    xPos = layers[0].Width - holePanelWidth - featuresMarginX;

                    CalibrateExposureFinderShapes effectiveShape = _holeShape == CalibrateExposureFinderShapes.Square || diameter < 6 ?
                        CalibrateExposureFinderShapes.Square : CalibrateExposureFinderShapes.Circle;

                    switch (effectiveShape)
                    {
                        case CalibrateExposureFinderShapes.Square:
                            xPos -= diameter;
                            break;
                        case CalibrateExposureFinderShapes.Circle:
                            xPos -= radius;
                            yPos += radius;
                            break;
                    }


                    // Left side
                    if (layerIndex == 1)
                    {
                        if (diameter == 1)
                        {
                            layer.SetByte(xPos, yPos, 255);
                        }
                        else
                        {
                            switch (effectiveShape)
                            {
                                case CalibrateExposureFinderShapes.Square:
                                    CvInvoke.Rectangle(layers[layerIndex],
                                        new Rectangle(new Point(xPos, yPos), new Size(diameter-1, diameter-1)),
                                        EmguExtensions.WhiteColor, -1,
                                        _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                                    break;
                                case CalibrateExposureFinderShapes.Circle:
                                    CvInvoke.Circle(layers[layerIndex],
                                        new Point(xPos, yPos),
                                        radius, EmguExtensions.WhiteColor, -1,
                                        _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                                    break;
                            }

                        }
                    }

                    //holeXPos = layers[0].Width - holeXPos;
                    switch (effectiveShape)
                    {
                        case CalibrateExposureFinderShapes.Square:
                            xPos = layers[0].Width - rect.X - featuresMarginX - holes[^1];
                            break;
                        case CalibrateExposureFinderShapes.Circle:
                            xPos = layers[0].Width - rect.X - featuresMarginX - holes[^1] + radius;
                            break;
                    }

                    // Right side
                    if (diameter == 1)
                    {
                        layer.SetByte(xPos, yPos, 0);
                    }
                    else
                    {
                        switch (effectiveShape)
                        {
                            case CalibrateExposureFinderShapes.Square:
                                CvInvoke.Rectangle(layers[layerIndex],
                                    new Rectangle(new Point(xPos, yPos), new Size(diameter-1, diameter-1)),
                                    EmguExtensions.BlackColor, -1,
                                    _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                                break;
                            case CalibrateExposureFinderShapes.Circle:
                                CvInvoke.Circle(layers[layerIndex],
                                    new Point(xPos, yPos),
                                    radius, EmguExtensions.BlackColor, -1,
                                    _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                                break;
                        }
                    }


                    yPos += featuresMarginY;

                    switch (effectiveShape)
                    {
                        case CalibrateExposureFinderShapes.Square:
                            yPos += diameter;
                            break;
                        case CalibrateExposureFinderShapes.Circle:
                            yPos += radius;
                            break;
                    }
                }
            }

            xPos = featuresMarginX;
            
            // Print Zebra bars
            if (bars.Length > 0)
            {
                int yStartPos = _staircaseThickness + featuresMarginY;
                int xStartPos = xPos;
                yPos = yStartPos + _barFenceThickness / 2 + _barFenceOffset;
                xPos += _barFenceThickness / 2 + _barFenceOffset;
                for (int i = 0; i < bars.Length; i++)
                {
                    // Print positive bottom
                    CvInvoke.Rectangle(layers[1], new Rectangle(xPos, yPos, barLengthPx - 1, barSpacingPx - 1),
                        EmguExtensions.WhiteColor, -1, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                    // Print positive top
                    yPos += barSpacingPx;
                    CvInvoke.Rectangle(layers[1], new Rectangle(xPos + barLengthPx + _barVerticalSplitter, yPos, barLengthPx - 1, bars[i] - 1),
                        EmguExtensions.WhiteColor, -1, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                    yPos += bars[i];
                }

                // Left over
                CvInvoke.Rectangle(layers[1], new Rectangle(xPos, yPos, barLengthPx - 1, barSpacingPx - 1),
                    EmguExtensions.WhiteColor, -1, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);

                yPos += barSpacingPx;

                if (_barFenceThickness > 0)
                {
                    CvInvoke.Rectangle(layers[1], new Rectangle(
                            xStartPos - 1, 
                            yStartPos - 1, 
                            barsPanelWidth - _barFenceThickness + 1,
                            yPos - yStartPos + _barFenceThickness / 2 + _barFenceOffset + 1),
                        EmguExtensions.WhiteColor, _barFenceThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);

                    yPos += _barFenceThickness * 2 + _barFenceOffset * 2;
                }

                xPos += featuresMarginX;
            }

            if (!textSize.IsEmpty)
            {
                CvInvoke.Rotate(layers[1], layers[1], RotateFlags.Rotate90CounterClockwise);
                CvInvoke.PutText(layers[1], _text, new Point(_staircaseThickness + featuresMarginX, layers[1].Height - barsPanelWidth - featuresMarginX * (barsPanelWidth > 0 ? 2 : 1)), _textFont, _textScale, EmguExtensions.WhiteColor, _textThickness, _enableAntiAliasing ? LineType.AntiAlias :  LineType.EightConnected);
                CvInvoke.Rotate(layers[1], layers[1], RotateFlags.Rotate90Clockwise);
            }

            // Print bullseye
            if (bulleyes.Length > 0)
            {
                yPos = bullseyeYPos;
                foreach (var circle in bulleyes)
                {
                    CvInvoke.Circle(layers[1], new Point(bullseyeXPos, yPos), circle.Radius, EmguExtensions.WhiteColor, circle.Thickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                }

                if (_bullsEyeInvertQuadrants)
                {
                    var matRoi1 = new Mat(layers[1], new Rectangle(bullseyeXPos, yPos - bulleyesRadius - 5, bulleyesRadius + 6, bulleyesRadius + 5));
                    var matRoi2 = new Mat(layers[1], new Rectangle(bullseyeXPos - bulleyesRadius - 5, yPos, bulleyesRadius + 5, bulleyesRadius + 6));
                    //using var mask = matRoi1.CloneBlank();

                    //CvInvoke.Circle(mask, new Point(mask.Width / 2, mask.Height / 2), bulleyesRadius, EmguExtensions.WhiteByte, -1, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                    //CvInvoke.Circle(mask, new Point(mask.Width / 2, mask.Height / 2), BullsEyes[^1].Radius, EmguExtensions.WhiteByte, BullsEyes[^1].Thickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);

                    CvInvoke.BitwiseNot(matRoi1, matRoi1);
                    CvInvoke.BitwiseNot(matRoi2, matRoi2);
                }

                if (_bullsEyeFenceThickness > 0)
                {
                    CvInvoke.Rectangle(layers[1],
                        new Rectangle(
                            new Point(
                                bullseyeXPos - bulleyesRadius - 5 - _bullsEyeFenceOffset - _bullsEyeFenceThickness / 2, 
                                yPos - bulleyesRadius - 5 - _bullsEyeFenceOffset - _bullsEyeFenceThickness / 2), 
                            new Size(
                                bulleyesDiameter + 10 + _bullsEyeFenceOffset*2 + _bullsEyeFenceThickness, 
                                bulleyesDiameter + 10 + _bullsEyeFenceOffset*2 + _bullsEyeFenceThickness)), 
                        EmguExtensions.WhiteColor,
                        _bullsEyeFenceThickness, 
                        _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                }
                

                yPos += bulleyesRadius;
            }

            if (isPreview)
            {
                var textHeightStart = layers[1].Height - featuresMarginY - TextMarkingSpacing;
                CvInvoke.PutText(layers[1], $"{Microns}u", new Point(TextMarkingStartX, textHeightStart), TextMarkingFontFace, TextMarkingScale, EmguExtensions.WhiteColor, TextMarkingThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                CvInvoke.PutText(layers[1], $"{_bottomExposure}s", new Point(TextMarkingStartX, textHeightStart + TextMarkingLineBreak), TextMarkingFontFace, TextMarkingScale, EmguExtensions.WhiteColor, TextMarkingThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                CvInvoke.PutText(layers[1], $"{_normalExposure}s", new Point(TextMarkingStartX, textHeightStart + TextMarkingLineBreak * 2), TextMarkingFontFace, TextMarkingScale, EmguExtensions.WhiteColor, TextMarkingThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                if (holes.Length > 0)
                {
                    CvInvoke.PutText(layers[1], $"{Microns}u", new Point(layers[1].Width - featuresMarginX * 2 - holes[^1] + TextMarkingStartX, textHeightStart), TextMarkingFontFace, TextMarkingScale, EmguExtensions.BlackColor, TextMarkingThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                    CvInvoke.PutText(layers[1], $"{_bottomExposure}s", new Point(layers[1].Width - featuresMarginX * 2 - holes[^1] + TextMarkingStartX, textHeightStart + TextMarkingLineBreak), TextMarkingFontFace, TextMarkingScale, EmguExtensions.BlackColor, TextMarkingThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                    CvInvoke.PutText(layers[1], $"{_normalExposure}s", new Point(layers[1].Width - featuresMarginX * 2 - holes[^1] + TextMarkingStartX, textHeightStart + TextMarkingLineBreak * 2), TextMarkingFontFace, TextMarkingScale, EmguExtensions.BlackColor, TextMarkingThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                }
            }

            if (negativeSideWidth >= 200 && _counterTrianglesEnabled)
            {
                xPos = 120;
                int triangleHeight = TextMarkingSpacing + 19;
                int triangleWidth = (negativeSideWidth - xPos - featuresMarginX) / 2;
                int triangleWidthQuarter = triangleWidth / 4;

                if (triangleWidth > 5)
                {
                    yPos = layers[1].Height - featuresMarginY - triangleHeight + 1;
                    int yHalfPos = yPos + triangleHeight / 2;
                    int yPosEnd = layers[1].Height - featuresMarginY + 1;

                    var triangles = new Point[4][];

                    triangles[0] = new Point[]  // Left
                    {
                        new(xPos, yPos), // Top Left
                        new(xPos + triangleWidth, yHalfPos), // Middle
                        new(xPos, yPosEnd), // Bottom Left
                    };
                    triangles[1] = new Point[] // Right
                    {
                        new(xPos + triangleWidth * 2, yPos), // Top Right
                        new(xPos + triangleWidth, yHalfPos), // Middle
                        new(xPos + triangleWidth * 2, yPosEnd), // Bottom Right
                    };
                    triangles[2] = new Point[] // Top
                    {
                        new(xPos + triangleWidth - triangleWidthQuarter, yPos),  // Top Left
                        new(xPos + triangleWidth + triangleWidthQuarter, yPos),  // Top Right
                        new(xPos + triangleWidth, yHalfPos - _counterTrianglesTipOffset), // Middle
                    };
                    triangles[3] = new Point[] // Bottom
                    {
                        new(xPos + triangleWidth - triangleWidthQuarter, yPosEnd),  // Bottom Left
                        new(xPos + triangleWidth + triangleWidthQuarter, yPosEnd),  // Bottom Right
                        new(xPos + triangleWidth, yHalfPos + _counterTrianglesTipOffset), // Middle
                    };

                    foreach (var triangle in triangles)
                    {
                        using var vec = new VectorOfPoint(triangle);
                        CvInvoke.FillPoly(layers[1], vec, EmguExtensions.WhiteColor,
                            _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                    }

                    /*byte size = 60;
                    var matRoi = new Mat(layers[1], new Rectangle(
                        new Point(xPos + triangleWidth - size / 2, yHalfPos - size / 2),
                        new Size(size, size)));

                    CvInvoke.BitwiseNot(matRoi, matRoi);*/
                    

                    if (_counterTrianglesFence)
                    {
                        byte outlineThickness = 8;
                        //byte outlineThicknessHalf = (byte)(outlineThickness / 2);

                        CvInvoke.Rectangle(layers[1], new Rectangle(
                                new Point(triangles[0][0].X - 0, triangles[0][0].Y - 0),
                                new Size(triangleWidth * 2 + 0, triangleHeight + 0)
                            ), EmguExtensions.WhiteColor, outlineThickness,
                            _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                    }
                }
            }
            // Print a hardcoded spiral if have space
            /*if (positiveSideWidth >= 250000)
            {
                var mat = layers[0].CloneBlank();
                var matMask = layers[0].CloneBlank();
                xPos = (int) ((layers[0].Width - holePanelWidth) / 1.8);
                yPos = layers[0].Height - featuresMarginY - TextMarkingSpacing / 2;
                byte circleThickness = 5;
                byte radiusStep = 13;
                int count = -1;
                int maxRadius = 0;
                //bool white = true;

                for (int radius = radiusStep;radius <= 100; radius += (radiusStep + count))
                {
                    count++;
                    CvInvoke.Circle(mat, new Point(xPos, yPos), radius, EmguExtensions.WhiteByte, circleThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                    maxRadius = radius;
                }

                CvInvoke.Circle(mat, new Point(xPos, yPos), 5, EmguExtensions.WhiteByte, -1, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                CvInvoke.Circle(matMask, new Point(xPos, yPos), maxRadius+2, EmguExtensions.WhiteByte, -1);

                var matRoi1 = new Mat(mat, new Rectangle(xPos, yPos - maxRadius-1, maxRadius+2, maxRadius+1));
                var matRoi2 = new Mat(mat, new Rectangle(xPos-maxRadius-1, yPos, maxRadius+1, Math.Min(mat.Height- yPos, maxRadius)));

                CvInvoke.BitwiseNot(matRoi1, matRoi1);
                CvInvoke.BitwiseNot(matRoi2, matRoi2);

                CvInvoke.BitwiseAnd(layers[0], mat, layers[1], matMask);

                Point anchor = new Point(-1, -1);
                //CvInvoke.MorphologyEx(layers[1], layers[1], MorphOp.Open, CvInvoke.GetStructuringElement(ElementShape.Rectangle, new Size(3,3), anchor), anchor, 1, BorderType.Reflect101, default);
                
                mat.Dispose();
                matMask.Dispose();
            }*/

            return layers;
        }

        public Mat GetThumbnail()
        {
            Mat thumbnail = EmguExtensions.InitMat(new Size(400, 200), 3);
            var fontFace = FontFace.HersheyDuplex;
            var fontScale = 1;
            var fontThickness = 2;
            const byte xSpacing = 45;
            const byte ySpacing = 45;
            CvInvoke.PutText(thumbnail, "UVtools", new Point(140, 35), fontFace, fontScale, new MCvScalar(255, 27, 245), fontThickness + 1);
            CvInvoke.Line(thumbnail, new Point(xSpacing, 0), new Point(xSpacing, ySpacing + 5), new MCvScalar(255, 27, 245), 3);
            CvInvoke.Line(thumbnail, new Point(xSpacing, ySpacing + 5), new Point(thumbnail.Width - xSpacing, ySpacing + 5), new MCvScalar(255, 27, 245), 3);
            CvInvoke.Line(thumbnail, new Point(thumbnail.Width - xSpacing, 0), new Point(thumbnail.Width - xSpacing, ySpacing + 5), new MCvScalar(255, 27, 245), 3);
            CvInvoke.PutText(thumbnail, "Exposure Time Cal.", new Point(xSpacing, ySpacing * 2), fontFace, fontScale, new MCvScalar(0, 255, 255), fontThickness);
            CvInvoke.PutText(thumbnail, $"{Microns}um @ {BottomExposure}s/{NormalExposure}s", new Point(xSpacing, ySpacing * 3), fontFace, fontScale, EmguExtensions.WhiteColor, fontThickness);
            if (_patternModel)
            {
                CvInvoke.PutText(thumbnail, $"Patterned Model", new Point(xSpacing, ySpacing * 4), fontFace, fontScale, EmguExtensions.WhiteColor, fontThickness);
            }
            else
            {
                CvInvoke.PutText(thumbnail, $"Features: {(_staircaseThickness > 0 ? 1 : 0) + Holes.Length + Bars.Length + BullsEyes.Length + (_counterTrianglesEnabled ? 1 : 0)}", new Point(xSpacing, ySpacing * 4), fontFace, fontScale, EmguExtensions.WhiteColor, fontThickness);
            }
            

            return thumbnail;
        }

        protected override bool ExecuteInternally(OperationProgress progress)
        {
            int sideMarginPx = (int)Math.Floor(_leftRightMargin * Xppmm);
            int topBottomMarginPx = (int)Math.Floor(_topBottomMargin * Yppmm);
            int partMarginXPx = (int) Math.Floor(_partMargin * Xppmm);
            int partMarginYPx = (int) Math.Floor(_partMargin * Yppmm);

            var anchor = new Point(-1, -1);
            using var kernel = CvInvoke.GetStructuringElement(ElementShape.Rectangle, new Size(3, 3), anchor);

            if (_patternModel)
            {
                ConcurrentBag<Layer> parallelLayers = new();
                Dictionary<ExposureItem, Point> table = new();
                var boundingRectangle = SlicerFile.BoundingRectangle;
                int xHalf = boundingRectangle.Width / 2;
                int yHalf = boundingRectangle.Height / 2;

                var brightnesses = MultipleBrightnessValuesArray;
                var multipleExposures = _exposureTable.Where(item => item.IsValid && item.LayerHeight == (decimal) SlicerFile.LayerHeight).ToArray();
                if (brightnesses.Length == 0 || !_multipleBrightness) brightnesses = new[] { byte.MaxValue };
                if (multipleExposures.Length == 0 || !_multipleExposures) multipleExposures = new[] { new ExposureItem((decimal)SlicerFile.LayerHeight, _bottomExposure, _normalExposure) };

                int currentX = sideMarginPx;
                int currentY = topBottomMarginPx;
                Rectangle glueBottomLayerRectangle = new(new Point(currentX, currentY), Size.Empty);
                foreach (var multipleExposure in multipleExposures)
                {
                    foreach (var brightness in brightnesses)
                    {
                        if (currentX + boundingRectangle.Width + sideMarginPx >= SlicerFile.ResolutionX)
                        {
                            currentX = sideMarginPx;
                            currentY += boundingRectangle.Height + partMarginYPx;
                        }

                        if (currentY + topBottomMarginPx >= SlicerFile.ResolutionY) break;

                        var item = multipleExposure.Clone();
                        item.Brightness = brightness;
                        table.Add(item, new Point(currentX, currentY));

                        glueBottomLayerRectangle.Size = new Size(currentX + boundingRectangle.Width, currentY + boundingRectangle.Height);

                        currentX += boundingRectangle.Width + partMarginXPx;
                    }
                }

                if (table.Count <= 1) return false;
                ushort microns = SlicerFile.LayerHeightUm;

                var tableGrouped = table.GroupBy(pair => new {pair.Key.LayerHeight, pair.Key.BottomExposure, pair.Key.Exposure}).Distinct();
                SlicerFile.BottomLayerCount = _bottomLayers;
                progress.ItemCount = (uint) (SlicerFile.LayerCount * table.Count); 
                Parallel.For(0, SlicerFile.LayerCount, layerIndex =>
                {
                    if (progress.Token.IsCancellationRequested) return;
                    var layer = SlicerFile[layerIndex];
                    using var mat = layer.LayerMat;
                    var matRoi = new Mat(mat, boundingRectangle);
                    int layerCountOnHeight = (int)Math.Floor(layer.PositionZ / SlicerFile.LayerHeight);
                    foreach (var group in tableGrouped)
                    {
                        var newLayer = layer.Clone();
                        newLayer.ExposureTime = (float)(newLayer.IsBottomLayer ? group.Key.BottomExposure : group.Key.Exposure);
                        using var newMat = mat.NewBlank();
                        foreach (var brightness in brightnesses)
                        {
                            ExposureItem item = new(group.Key.LayerHeight, group.Key.BottomExposure, group.Key.Exposure, brightness);
                            if(!table.TryGetValue(item, out var point)) continue;
                            
                            var newMatRoi = new Mat(newMat, new Rectangle(point, matRoi.Size));
                            matRoi.CopyTo(newMatRoi);

                            if (layer.IsBottomLayer)
                            {
                                if (_patternModelGlueBottomLayers)
                                {
                                    newMatRoi.SetTo(EmguExtensions.WhiteColor);
                                }
                            }

                            if (layerCountOnHeight < _chamferLayers)
                            {
                                CvInvoke.Erode(newMatRoi, newMatRoi, kernel, anchor, _chamferLayers - layerCountOnHeight, BorderType.Reflect101, default);
                            }

                            if (layer.IsBottomLayer)
                            {
                                if (_erodeBottomIterations > 0)
                                {
                                    CvInvoke.Erode(newMatRoi, newMatRoi, kernel, anchor, _erodeBottomIterations, BorderType.Reflect101, default);
                                }

                                if (_multipleBrightness)
                                {
                                    CvInvoke.PutText(newMatRoi, brightness.ToString(), new(xHalf - 60, yHalf + 20 - TextMarkingLineBreak * 4), TextMarkingFontFace, 2, EmguExtensions.BlackColor, 3, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                                }
                                CvInvoke.PutText(newMatRoi, $"{microns}u", new(xHalf - 60, yHalf + 20 - TextMarkingLineBreak * 2), TextMarkingFontFace, 2, EmguExtensions.BlackColor, 3, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                                CvInvoke.PutText(newMatRoi, $"{group.Key.BottomExposure}s", new(xHalf - 60, yHalf + 20), TextMarkingFontFace, 2, EmguExtensions.BlackColor, 3, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                                CvInvoke.PutText(newMatRoi, $"{group.Key.Exposure}s", new(xHalf - 60, yHalf + 20 + TextMarkingLineBreak * 2), TextMarkingFontFace, 2, EmguExtensions.BlackColor, 3, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                            }

                            if (brightness < 255)
                            {
                                if (_multipleBrightnessExcludeFrom == CalibrateExposureFinderMultipleBrightnessExcludeFrom.None ||
                                    _multipleBrightnessExcludeFrom == CalibrateExposureFinderMultipleBrightnessExcludeFrom.Bottom && !layer.IsBottomLayer ||
                                    _multipleBrightnessExcludeFrom == CalibrateExposureFinderMultipleBrightnessExcludeFrom.BottomAndBase && !layer.IsBottomLayer)
                                {
                                    using var pattern = matRoi.New();
                                    pattern.SetTo(new MCvScalar(byte.MaxValue - brightness));
                                    CvInvoke.Subtract(newMatRoi, pattern, newMatRoi);
                                }
                            }
                        }
                        

                        newLayer.LayerMat = newMat;
                        parallelLayers.Add(newLayer);
                        progress.LockAndIncrement();
                    }
                });

                progress.Token.ThrowIfCancellationRequested();
                if (parallelLayers.IsEmpty) return false;
                var layers = parallelLayers.OrderBy(layer => layer.PositionZ).ThenBy(layer => layer.ExposureTime).ToList();

                progress.ResetNameAndProcessed("Optimized layers");

                Layer currentLayer = layers[0];
                for (var layerIndex = 1; layerIndex < layers.Count; layerIndex++)
                {
                    progress.Token.ThrowIfCancellationRequested();
                    progress++;
                    var layer = layers[layerIndex];
                    if (currentLayer.PositionZ != layer.PositionZ ||
                        currentLayer.ExposureTime != layer.ExposureTime) // Different layers, cache and continue
                    {
                        currentLayer = layer;
                        continue;
                    }

                    using var matCurrent = currentLayer.LayerMat;
                    using var mat = layer.LayerMat;

                    CvInvoke.Add(matCurrent, mat, matCurrent); // Sum layers
                    currentLayer.LayerMat = matCurrent;

                    layers[layerIndex] = null; // Discard
                }

                layers.RemoveAll(layer => layer is null); // Discard equal layers

                SlicerFile.SuppressRebuildPropertiesWork(() =>
                {
                    SlicerFile.BottomExposureTime = (float)BottomExposure;
                    SlicerFile.ExposureTime = (float)NormalExposure;
                    SlicerFile.LayerManager.Layers = layers.ToArray();
                });
            }
            else // No patterned
            {
                var layers = GetLayers();
                if (layers is null) return false;
                progress.ItemCount = 0;
                //SanitizeExposureTable();
                if (layers[0].Width > SlicerFile.ResolutionX || layers[0].Height > SlicerFile.ResolutionY)
                {
                    return false;
                }

                List<Layer> newLayers = new();

                Dictionary<ExposureItem, Point> table = new();
                var endLayerHeight = _multipleLayerHeight ? _multipleLayerHeightMaximum : _layerHeight;
                var totalHeight = TotalHeight;
                uint layerIndex = 0;
                int currentX = sideMarginPx;
                int currentY = topBottomMarginPx;
                int featuresMarginX = (int)(Xppmm * _featuresMargin);
                int featuresMarginY = (int)(Yppmm * _featuresMargin);

                var holes = Holes;
                int holePanelWidth = holes.Length > 0 ? featuresMarginX * 2 + holes[^1] : 0;
                int staircaseWidth = layers[0].Width - holePanelWidth;

                var brightnesses = MultipleBrightnessValuesArray;
                if (brightnesses.Length == 0 || !_multipleBrightness) brightnesses = new[] { byte.MaxValue };

                ExposureItem lastExposureItem = null;
                decimal lastcurrentHeight = 0;

                void AddLayer(decimal currentHeight, decimal layerHeight, decimal bottomExposure, decimal normalExposure)
                {
                    var layerDifference = currentHeight / layerHeight;

                    if (!layerDifference.IsInteger()) return; // Not at right height to process with layer height
                                                              //Debug.WriteLine($"{currentHeight} / {layerHeight} = {layerDifference}, Floor={Math.Floor(layerDifference)}");

                    int firstFeatureLayer = (int)Math.Floor(_baseHeight / layerHeight);
                    int lastLayer = (int)Math.Floor((_baseHeight + _featuresHeight) / layerHeight);
                    int layerCountOnHeight = (int)Math.Floor(currentHeight / layerHeight);
                    bool isBottomLayer = layerCountOnHeight <= _bottomLayers;
                    bool isBaseLayer = currentHeight <= _baseHeight;
                    ushort microns = (ushort)Math.Floor(layerHeight * 1000);
                    Point position;
                    bool addSomething = false;

                    bool reUseLastLayer =
                        lastExposureItem is not null &&
                        lastcurrentHeight == currentHeight &&
                        lastExposureItem.LayerHeight == layerHeight &&
                        (isBottomLayer && lastExposureItem.BottomExposure == bottomExposure || !isBottomLayer && lastExposureItem.Exposure == normalExposure);

                    using var mat = reUseLastLayer ? newLayers[^1].LayerMat : EmguExtensions.InitMat(SlicerFile.Resolution);

                    lastcurrentHeight = currentHeight;

                    foreach (var brightness in brightnesses)
                    {
                        var bottomExposureTemp = bottomExposure;
                        var normalExposureTemp = normalExposure;
                        ExposureItem key = new(layerHeight, bottomExposure, normalExposure, brightness);
                        lastExposureItem = key;

                        if (table.TryGetValue(key, out var pos))
                        {
                            position = pos;
                        }
                        else
                        {
                            if (currentX + layers[0].Width + sideMarginPx > SlicerFile.ResolutionX)
                            {
                                currentX = sideMarginPx;
                                currentY += layers[0].Height + partMarginYPx;
                            }

                            if (currentY + layers[0].Height + topBottomMarginPx > SlicerFile.ResolutionY)
                            {
                                break; // Reach the end
                            }

                            position = new Point(currentX, currentY);
                            table.Add(key, new Point(currentX, currentY));

                            currentX += layers[0].Width + partMarginXPx;
                        }


                        Mat matRoi = new(mat, new Rectangle(position, layers[0].Size));

                        layers[isBaseLayer ? 0 : 1].CopyTo(matRoi);

                        if (!isBaseLayer && _staircaseThickness > 0)
                        {
                            int staircaseWidthIncrement = (int) Math.Ceiling(staircaseWidth / (_featuresHeight / layerHeight-1));
                            int staircaseLayer = layerCountOnHeight - firstFeatureLayer - 1;
                            int staircaseWidthForLayer = staircaseWidth - staircaseWidthIncrement * staircaseLayer;
                            if (staircaseWidthForLayer >= 0 && layerCountOnHeight != lastLayer)
                            {
                                CvInvoke.Rectangle(matRoi,
                                    new Rectangle(staircaseWidth - staircaseWidthForLayer, 0, staircaseWidthForLayer, _staircaseThickness),
                                    EmguExtensions.WhiteColor, -1,
                                    _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                            }
                        }

                        if (isBottomLayer && _erodeBottomIterations > 0)
                        {
                            CvInvoke.Erode(matRoi, matRoi, kernel, anchor, _erodeBottomIterations, BorderType.Reflect101, default);
                        }

                        if (layerCountOnHeight < _chamferLayers)
                        {
                            CvInvoke.Erode(matRoi, matRoi, kernel, anchor, _chamferLayers - layerCountOnHeight, BorderType.Reflect101, default);
                        }

                        if (_multipleBrightness && brightness < 255)
                        {
                            // normalExposure - 255
                            //       x        - brightness
                            normalExposureTemp = Math.Round(normalExposure * brightness / byte.MaxValue, 2);
                            if (_multipleBrightnessExcludeFrom == CalibrateExposureFinderMultipleBrightnessExcludeFrom.None)
                            {
                                bottomExposureTemp = Math.Round(bottomExposure * brightness / byte.MaxValue, 2);
                            }
                        }

                        var textHeightStart = matRoi.Height - featuresMarginY - TextMarkingSpacing;
                        CvInvoke.PutText(matRoi, $"{microns}u", new Point(TextMarkingStartX, textHeightStart), TextMarkingFontFace, TextMarkingScale, EmguExtensions.WhiteColor, TextMarkingThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                        CvInvoke.PutText(matRoi, $"{bottomExposureTemp}s", new Point(TextMarkingStartX, textHeightStart + TextMarkingLineBreak), TextMarkingFontFace, TextMarkingScale, EmguExtensions.WhiteColor, TextMarkingThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                        CvInvoke.PutText(matRoi, $"{normalExposureTemp}s", new Point(TextMarkingStartX, textHeightStart + TextMarkingLineBreak * 2), TextMarkingFontFace, TextMarkingScale, EmguExtensions.WhiteColor, TextMarkingThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                        if (holes.Length > 0)
                        {
                            CvInvoke.PutText(matRoi, $"{microns}u", new Point(matRoi.Width - featuresMarginX * 2 - holes[^1] + TextMarkingStartX, textHeightStart), TextMarkingFontFace, TextMarkingScale, EmguExtensions.BlackColor, TextMarkingThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                            CvInvoke.PutText(matRoi, $"{bottomExposureTemp}s", new Point(matRoi.Width - featuresMarginX * 2 - holes[^1] + TextMarkingStartX, textHeightStart + TextMarkingLineBreak), TextMarkingFontFace, TextMarkingScale, EmguExtensions.BlackColor, TextMarkingThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                            CvInvoke.PutText(matRoi, $"{normalExposureTemp}s", new Point(matRoi.Width - featuresMarginX * 2 - holes[^1] + TextMarkingStartX, textHeightStart + TextMarkingLineBreak * 2), TextMarkingFontFace, TextMarkingScale, EmguExtensions.BlackColor, TextMarkingThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                        }

                        if (_multipleBrightness)
                        {
                            CvInvoke.PutText(matRoi, brightness.ToString(), new Point(matRoi.Width / 3, 35), TextMarkingFontFace, TextMarkingScale, EmguExtensions.WhiteColor, TextMarkingThickness, _enableAntiAliasing ? LineType.AntiAlias : LineType.EightConnected);
                            if (brightness < 255 &&
                                (_multipleBrightnessExcludeFrom == CalibrateExposureFinderMultipleBrightnessExcludeFrom.None ||
                                 _multipleBrightnessExcludeFrom == CalibrateExposureFinderMultipleBrightnessExcludeFrom.Bottom && !isBottomLayer ||
                                 _multipleBrightnessExcludeFrom == CalibrateExposureFinderMultipleBrightnessExcludeFrom.BottomAndBase && !isBottomLayer && !isBaseLayer)
                            )
                            {
                                using var pattern = matRoi.New();
                                //pattern.SetTo(new MCvScalar(brightness)); OLD
                                //CvInvoke.BitwiseAnd(matRoi, pattern, matRoi, matRoi); OLD

                                pattern.SetTo(new MCvScalar(byte.MaxValue - brightness));
                                CvInvoke.Subtract(matRoi, pattern, matRoi);
                            }
                        }

                        addSomething = true;
                    }

                    if (!addSomething) return;

                    if (reUseLastLayer)
                    {
                        newLayers[^1].LayerMat = mat;
                    }
                    else
                    {
                        Layer layer = new(layerIndex++, mat, SlicerFile)
                        {
                            PositionZ = (float)currentHeight,
                            ExposureTime = isBottomLayer ? (float)bottomExposure : (float)normalExposure,
                            LiftHeight = isBottomLayer ? SlicerFile.BottomLiftHeight : SlicerFile.LiftHeight,
                            LiftSpeed = isBottomLayer ? SlicerFile.BottomLiftSpeed : SlicerFile.LiftSpeed,
                            RetractSpeed = SlicerFile.RetractSpeed,
                            LightOffDelay = isBottomLayer ? SlicerFile.BottomLightOffDelay : SlicerFile.LightOffDelay,
                            LightPWM = isBottomLayer ? SlicerFile.BottomLightPWM : SlicerFile.LightPWM,
                            IsModified = true
                        };
                        newLayers.Add(layer);
                    }
                    

                    progress++;
                }

                for (decimal currentHeight = _layerHeight; currentHeight <= totalHeight; currentHeight += Layer.HeightPrecisionIncrement)
                {
                    currentHeight = Layer.RoundHeight(currentHeight);
                    for (decimal layerHeight = _layerHeight; layerHeight <= endLayerHeight; layerHeight += _multipleLayerHeightStep)
                    {
                        progress.Token.ThrowIfCancellationRequested();
                        layerHeight = Layer.RoundHeight(layerHeight);

                        if (_multipleExposures)
                        {
                            foreach (var exposureItem in _exposureTable)
                            {
                                if (exposureItem.IsValid && exposureItem.LayerHeight == layerHeight)
                                {
                                    AddLayer(currentHeight, layerHeight, exposureItem.BottomExposure, exposureItem.Exposure);
                                }
                            }
                        }
                        else
                        {
                            AddLayer(currentHeight, layerHeight, _bottomExposure, _normalExposure);
                        }
                    }
                }

                SlicerFile.SuppressRebuildPropertiesWork(() =>
                {
                    SlicerFile.LayerHeight = (float)LayerHeight;
                    SlicerFile.BottomExposureTime = (float)BottomExposure;
                    SlicerFile.ExposureTime = (float)NormalExposure;
                    SlicerFile.BottomLayerCount = BottomLayers;
                    SlicerFile.LayerManager.Layers = newLayers.ToArray();
                });

                if (_mirrorOutput)
                {
                    new OperationFlip(SlicerFile) { FlipDirection = Enumerations.FlipDirection.Horizontally }.Execute(progress);
                }
            }

            if (SlicerFile.ThumbnailsCount > 0)
                SlicerFile.SetThumbnails(GetThumbnail());

            if (_dontLiftSamePositionedLayers)
            {
                SlicerFile.LayerManager.SetNoLiftForSamePositionedLayers(_zeroLightOffSamePositionedLayers);
            }

            new OperationMove(SlicerFile).Execute(progress);

            return !progress.Token.IsCancellationRequested;
        }

        #endregion
    }
}
