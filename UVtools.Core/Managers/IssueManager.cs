﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using UVtools.Core.EmguCV;
using UVtools.Core.Extensions;
using UVtools.Core.FileFormats;
using UVtools.Core.Layers;
using UVtools.Core.Operations;

namespace UVtools.Core.Managers
{
    public sealed class IssueManager : RangeObservableCollection<MainIssue>
    {
        public FileFormat SlicerFile { get; }

        public readonly List<MainIssue> IgnoredIssues = new();

        public IssueManager(FileFormat slicerFile)
        {
            SlicerFile = slicerFile;
        }

        public static Issue[] GetIssues(IEnumerable<MainIssue> issues)
        {
            var result = new List<Issue>();
            foreach (var mainIssue in issues)
            {
                result.AddRange(mainIssue);
            }

            return result.ToArray();
        }

        public static Issue[] GetIssuesBy(IEnumerable<MainIssue> issues, MainIssue.IssueType type, uint layerIndex)
        {
            var result = new List<Issue>();
            foreach (var mainIssue in issues)
            {
                if (mainIssue.Type != type) continue;
                if (!mainIssue.IsIssueInBetween(layerIndex)) continue;
                foreach (var issue in mainIssue)
                {
                    if (issue.LayerIndex != layerIndex) continue;
                    result.Add(issue);
                }
            }

            return result.ToArray();
        }

        public static Issue[] GetIssuesBy(IEnumerable<MainIssue> issues, MainIssue.IssueType type)
        {
            var result = new List<Issue>();
            foreach (var mainIssue in issues)
            {
                if (mainIssue.Type != type) continue;
                result.AddRange(mainIssue);
            }

            return result.ToArray();
        }


        public static Issue[] GetIssuesBy(IEnumerable<MainIssue> issues, uint layerIndex)
        {
            var result = new List<Issue>();
            foreach (var mainIssue in issues)
            {
                if (!mainIssue.IsIssueInBetween(layerIndex)) continue;
                foreach (var issue in mainIssue)
                {
                    if (issue.LayerIndex != layerIndex) continue;
                    result.Add(issue);
                }
            }

            return result.ToArray();
        }

        public Issue[] GetIssues()
        {
            return GetIssues(this);
        }

        public Issue[] GetIssuesBy(MainIssue.IssueType type)
        {
            return GetIssuesBy(this, type);
        }

        public Issue[] GetIssuesBy(MainIssue.IssueType type, uint layerIndex)
        {
            return GetIssuesBy(this, type, layerIndex);
        }

        public Issue[] GetIssuesBy(uint layerIndex)
        {
            return GetIssuesBy(this, layerIndex);
        }

        public List<MainIssue> DetectIssues(
            IslandDetectionConfiguration islandConfig = null,
            OverhangDetectionConfiguration overhangConfig = null,
            ResinTrapDetectionConfiguration resinTrapConfig = null,
            TouchingBoundDetectionConfiguration touchBoundConfig = null,
            PrintHeightDetectionConfiguration printHeightConfig = null,
            bool emptyLayersConfig = true,
            OperationProgress progress = null)
        { 
            islandConfig ??= new IslandDetectionConfiguration();
            overhangConfig ??= new OverhangDetectionConfiguration();
            resinTrapConfig ??= new ResinTrapDetectionConfiguration();
            touchBoundConfig ??= new TouchingBoundDetectionConfiguration();
            printHeightConfig ??= new PrintHeightDetectionConfiguration();
            progress ??= new OperationProgress();

            var result = new ConcurrentBag<MainIssue>();
            //var layerHollowAreas = new ConcurrentDictionary<uint, List<LayerHollowArea>>();
            var resinTraps = new List<VectorOfVectorOfPoint>[SlicerFile.LayerCount];
            var suctionTraps = new List<VectorOfVectorOfPoint>[SlicerFile.LayerCount];
            var externalContours = new VectorOfVectorOfPoint[SlicerFile.LayerCount];
            var hollows = new List<VectorOfVectorOfPoint>[SlicerFile.LayerCount];
            var airContours = new List<VectorOfVectorOfPoint>[SlicerFile.LayerCount];
            var resinTrapsContoursArea = new double[SlicerFile.LayerCount][];

            bool IsIgnored(MainIssue issue) => IgnoredIssues.Count > 0 && IgnoredIssues.Contains(issue);

            bool AddIssue(MainIssue issue)
            {
                if (IsIgnored(issue)) return false;
                result.Add(issue);
                return true;
            }

            List<MainIssue> GetResult()
            {
                return result.OrderBy(issue => issue.Type).ThenBy(issue => issue.StartLayerIndex).ThenByDescending(issue => issue.Area).ToList();
            }

            void GenerateAirMap(IInputArray input, IInputOutputArray output, VectorOfVectorOfPoint externals)
            {
                CvInvoke.BitwiseNot(input, output);
                if (externals is null || externals.Size == 0) return;
                CvInvoke.DrawContours(output, externals, -1, EmguExtensions.BlackColor, -1);
            }

            if (printHeightConfig.Enabled && SlicerFile.MachineZ > 0)
            {
                float printHeightWithOffset = Layer.RoundHeight(SlicerFile.MachineZ + printHeightConfig.Offset);
                if (SlicerFile.PrintHeight > printHeightWithOffset)
                {
                    foreach (var layer in SlicerFile)
                    {
                        if (layer.PositionZ > printHeightWithOffset)
                        {
                            AddIssue(new MainIssue(MainIssue.IssueType.PrintHeight, new Issue(layer)));
                        }
                    }
                }
            }

            if (emptyLayersConfig)
            {
                foreach (var layer in SlicerFile)
                {
                    if (layer.IsEmpty)
                    {
                        AddIssue(new MainIssue(MainIssue.IssueType.EmptyLayer, new Issue(layer)));
                    }
                }
            }

            if (islandConfig.Enabled || overhangConfig.Enabled || resinTrapConfig.Enabled || touchBoundConfig.Enabled)
            {
                progress.Reset(OperationProgress.StatusIslands, SlicerFile.LayerCount);

                // Detect contours
                Parallel.For(0, SlicerFile.LayerCount, CoreSettings.ParallelOptions, layerIndexInt =>
                {
                    if (progress.Token.IsCancellationRequested) return;
                    uint layerIndex = (uint)layerIndexInt;
                    var layer = SlicerFile[layerIndex];

                    if (layer.IsEmpty)
                    {
                        progress.LockAndIncrement();
                        return;
                    }

                    // Spare a decoding cycle
                    if (!touchBoundConfig.Enabled &&
                        !resinTrapConfig.Enabled &&
                        (!overhangConfig.Enabled || overhangConfig.Enabled && (layerIndex == 0 || overhangConfig.WhiteListLayers is not null && !overhangConfig.WhiteListLayers.Contains(layerIndex))) &&
                        (!islandConfig.Enabled || islandConfig.Enabled && (layerIndex == 0 || islandConfig.WhiteListLayers is not null && !islandConfig.WhiteListLayers.Contains(layerIndex)))
                        )
                    {
                        progress.LockAndIncrement();

                        return;
                    }

                    using (var image = layer.LayerMat)
                    {
                        int step = image.Step;
                        var span = image.GetDataSpan<byte>();

                        if (touchBoundConfig.Enabled)
                        {
                            // TouchingBounds Checker
                            List<Point> pixels = new();
                            bool touchTop = layer.BoundingRectangle.Top <= touchBoundConfig.MarginTop;
                            bool touchBottom = layer.BoundingRectangle.Bottom >= image.Height - touchBoundConfig.MarginBottom;
                            bool touchLeft = layer.BoundingRectangle.Left <= touchBoundConfig.MarginLeft;
                            bool touchRight = layer.BoundingRectangle.Right >=
                                              image.Width - touchBoundConfig.MarginRight;
                            if (touchTop || touchBottom)
                            {
                                for (int x = 0; x < image.Width; x++) // Check Top and Bottom bounds
                                {
                                    if (touchTop)
                                    {
                                        for (int y = 0; y < touchBoundConfig.MarginTop; y++) // Top
                                        {
                                            if (span[image.GetPixelPos(x, y)] >=
                                                touchBoundConfig.MinimumPixelBrightness)
                                            {
                                                pixels.Add(new Point(x, y));
                                            }
                                        }
                                    }

                                    if (touchBottom)
                                    {
                                        for (int y = image.Height - touchBoundConfig.MarginBottom;
                                            y < image.Height;
                                            y++) // Bottom
                                        {
                                            if (span[image.GetPixelPos(x, y)] >=
                                                touchBoundConfig.MinimumPixelBrightness)
                                            {
                                                pixels.Add(new Point(x, y));
                                            }
                                        }
                                    }

                                }
                            }

                            if (touchLeft || touchRight)
                            {
                                for (int y = touchBoundConfig.MarginTop;
                                    y < image.Height - touchBoundConfig.MarginBottom;
                                    y++) // Check Left and Right bounds
                                {
                                    if (touchLeft)
                                    {
                                        for (int x = 0; x < touchBoundConfig.MarginLeft; x++) // Left
                                        {
                                            if (span[image.GetPixelPos(x, y)] >=
                                                touchBoundConfig.MinimumPixelBrightness)
                                            {
                                                pixels.Add(new Point(x, y));
                                            }
                                        }
                                    }

                                    if (touchRight)
                                    {
                                        for (int x = image.Width - touchBoundConfig.MarginRight;
                                            x < image.Width;
                                            x++) // Right
                                        {
                                            if (span[image.GetPixelPos(x, y)] >=
                                                touchBoundConfig.MinimumPixelBrightness)
                                            {
                                                pixels.Add(new Point(x, y));
                                            }
                                        }
                                    }
                                }
                            }

                            if (pixels.Count > 0)
                            {
                                AddIssue(new MainIssue(MainIssue.IssueType.TouchingBound, new IssueOfPoints(layer, pixels.ToArray())));
                            }
                        }

                        if (layerIndex > 0) // No islands nor overhangs for layer 0
                        {
                            Mat previousImage = null;
                            Span<byte> previousSpan = null;

                            if (islandConfig.Enabled)
                            {
                                bool canProcessCheck = true;
                                if (islandConfig.WhiteListLayers is not null) // Check white list
                                {
                                    if (!islandConfig.WhiteListLayers.Contains(layerIndex))
                                    {
                                        canProcessCheck = false;
                                    }
                                }

                                if (canProcessCheck)
                                {
                                    bool needDispose = false;
                                    Mat islandImage;
                                    if (islandConfig.BinaryThreshold > 0)
                                    {
                                        needDispose = true;
                                        islandImage = new();
                                        CvInvoke.Threshold(image, islandImage, islandConfig.BinaryThreshold, byte.MaxValue,
                                            ThresholdType.Binary);
                                    }
                                    else
                                    {
                                        islandImage = image;
                                    }

                                    using Mat labels = new();
                                    using Mat stats = new();
                                    using Mat centroids = new();
                                    var numLabels = CvInvoke.ConnectedComponentsWithStats(islandImage, labels, stats,
                                        centroids,
                                        islandConfig.AllowDiagonalBonds
                                            ? LineType.EightConnected
                                            : LineType.FourConnected);

                                    if (needDispose)
                                    {
                                        islandImage.Dispose();
                                    }

                                    // Get array that contains details of each connected component
                                    var ccStats = stats.GetData();
                                    //stats[i][0]: Left Edge of Connected Component
                                    //stats[i][1]: Top Edge of Connected Component 
                                    //stats[i][2]: Width of Connected Component
                                    //stats[i][3]: Height of Connected Component
                                    //stats[i][4]: Total Area (in pixels) in Connected Component

                                    var labelSpan = labels.GetDataSpan<int>();

                                    for (int i = 1; i < numLabels; i++)
                                    {
                                        Rectangle rect = new(
                                            (int)ccStats.GetValue(i, (int)ConnectedComponentsTypes.Left),
                                            (int)ccStats.GetValue(i, (int)ConnectedComponentsTypes.Top),
                                            (int)ccStats.GetValue(i, (int)ConnectedComponentsTypes.Width),
                                            (int)ccStats.GetValue(i, (int)ConnectedComponentsTypes.Height));

                                        if (rect.Area() < islandConfig.RequiredAreaToProcessCheck)
                                            continue;

                                        if (previousImage is null)
                                        {
                                            previousImage = SlicerFile[layerIndex - 1].LayerMat;
                                            previousSpan = previousImage.GetDataSpan<byte>();
                                        }

                                        List<Point> points = new();
                                        uint pixelsSupportingIsland = 0;

                                        for (int y = rect.Y; y < rect.Bottom; y++)
                                            for (int x = rect.X; x < rect.Right; x++)
                                            {
                                                int pixel = step * y + x;
                                                if (
                                                    labelSpan[pixel] !=
                                                    i || // Background pixel or a pixel from another component within the bounding rectangle
                                                    span[pixel] <
                                                    islandConfig
                                                        .RequiredPixelBrightnessToProcessCheck // Low brightness, ignore
                                                ) continue;

                                                points.Add(new Point(x, y));

                                                if (previousSpan[pixel] >=
                                                    islandConfig.RequiredPixelBrightnessToSupport)
                                                {
                                                    pixelsSupportingIsland++;
                                                }
                                            }

                                        if (points.Count == 0) continue; // Should never happen

                                        var requiredSupportingPixels = Math.Max(1,
                                            points.Count * islandConfig.RequiredPixelsToSupportMultiplier);

                                        /*if (pixelsSupportingIsland >= islandConfig.RequiredPixelsToSupport)
                                                isIsland = false; // Not a island, bounding is strong, i think...
                                            else if (pixelsSupportingIsland > 0 &&
                                                points.Count < islandConfig.RequiredPixelsToSupport &&
                                                pixelsSupportingIsland >= Math.Max(1, points.Count / 2))
                                                isIsland = false; // Not a island, but maybe weak bounding...*/

                                        IssueOfPoints island = null;
                                        if (pixelsSupportingIsland < requiredSupportingPixels)
                                        {
                                            island = new IssueOfPoints(layer, points.ToArray(), rect);
                                        }

                                        // Check for overhangs
                                        if (overhangConfig.Enabled && !overhangConfig.IndependentFromIslands &&
                                            island is null
                                            || island is not null && islandConfig.EnhancedDetection &&
                                            pixelsSupportingIsland >= 10
                                        )
                                        {
                                            points.Clear();
                                            using var imageRoi = image.Roi(rect);
                                            using var previousImageRoi = previousImage.Roi(rect);
                                            using var subtractedImage = new Mat();
                                            var anchor = new Point(-1, -1);
                                            CvInvoke.Subtract(imageRoi, previousImageRoi, subtractedImage);
                                            CvInvoke.Threshold(subtractedImage, subtractedImage, 127, 255,
                                                ThresholdType.Binary);

                                            CvInvoke.Erode(subtractedImage, subtractedImage,
                                                CvInvoke.GetStructuringElement(ElementShape.Rectangle,
                                                    new Size(3, 3), anchor),
                                                anchor, overhangConfig.ErodeIterations, BorderType.Default,
                                                new MCvScalar());

                                            var subtractedSpan = subtractedImage.GetDataSpan<byte>();

                                            for (int y = 0; y < subtractedImage.Height; y++)
                                                for (int x = 0; x < subtractedImage.Step; x++)
                                                {
                                                    int labelX = rect.X + x;
                                                    int labelY = rect.Y + y;
                                                    int pixel = subtractedImage.GetPixelPos(x, y);
                                                    int pixelLabel = labelY * step + labelX;
                                                    if (labelSpan[pixelLabel] != i || subtractedSpan[pixel] == 0)
                                                        continue;

                                                    points.Add(new Point(labelX, labelY));
                                                }

                                            if (points.Count >= overhangConfig.RequiredPixelsToConsider
                                            ) // Overhang
                                            {
                                                AddIssue(new MainIssue(MainIssue.IssueType.Overhang, new IssueOfPoints(layer, points.ToArray(), rect)));
                                            }
                                            else if (islandConfig.EnhancedDetection) // No overhang
                                            {
                                                island = null;
                                            }
                                        }

                                        if (island is not null)
                                            AddIssue(new MainIssue(MainIssue.IssueType.Island, island));
                                    }
                                }
                            }

                            // Overhangs
                            if (!islandConfig.Enabled && overhangConfig.Enabled ||
                                (islandConfig.Enabled && overhangConfig.Enabled &&
                                 overhangConfig.IndependentFromIslands))
                            {
                                bool canProcessCheck = true;
                                if (overhangConfig.WhiteListLayers is not null) // Check white list
                                {
                                    if (!overhangConfig.WhiteListLayers.Contains(layerIndex))
                                    {
                                        canProcessCheck = false;
                                    }
                                }

                                if (canProcessCheck)
                                {
                                    previousImage ??= SlicerFile[layerIndex - 1].LayerMat;

                                    using var subtractedImage = new Mat();
                                    using var vecPoints = new VectorOfPoint();
                                    var anchor = new Point(-1, -1);


                                    CvInvoke.Subtract(image, previousImage, subtractedImage);
                                    CvInvoke.Threshold(subtractedImage, subtractedImage, 127, 255,
                                        ThresholdType.Binary);

                                    CvInvoke.Erode(subtractedImage, subtractedImage,
                                        CvInvoke.GetStructuringElement(ElementShape.Rectangle, new Size(3, 3),
                                            anchor),
                                        anchor, overhangConfig.ErodeIterations, BorderType.Default,
                                        new MCvScalar());

                                    CvInvoke.FindNonZero(subtractedImage, vecPoints);
                                    if (vecPoints.Size >= overhangConfig.RequiredPixelsToConsider)
                                    {
                                        AddIssue(new MainIssue(MainIssue.IssueType.Overhang, new IssueOfPoints(layer, vecPoints.ToArray(), layer.BoundingRectangle)
                                        ));
                                    }
                                }
                            }

                            previousImage?.Dispose();
                        }

                        if (resinTrapConfig.Enabled)
                        {
                            /* this used to calculate all contours for the layers, however new algorithm crops the layers to the overall bounding box
                             * so the contours produced here are not translated properly. We will generate contours during the algorithm itself later */

                            bool needDispose = false;
                            Mat resinTrapImage;
                            if (resinTrapConfig.BinaryThreshold > 0)
                            {
                                resinTrapImage = new Mat();
                                CvInvoke.Threshold(image, resinTrapImage, resinTrapConfig.BinaryThreshold, byte.MaxValue, ThresholdType.Binary);
                            }
                            else
                            {
                                needDispose = true;
                                resinTrapImage = image;
                            }
                            var contourLayer = resinTrapImage.Roi(SlicerFile.BoundingRectangle);

                            using var contours = contourLayer.FindContours(out var heirarchy, RetrType.Tree);
                            externalContours[layerIndex] = EmguContours.GetExternalContours(contours, heirarchy);
                            hollows[layerIndex] = EmguContours.GetNegativeContoursInGroups(contours, heirarchy);
                            resinTrapsContoursArea[layerIndex] = EmguContours.GetContoursArea(hollows[layerIndex]);

                            if (needDispose)
                            {
                                resinTrapImage.Dispose();
                            }

                            /*//
                            //hierarchy[i][0]: the index of the next contour of the same level
                            //hierarchy[i][1]: the index of the previous contour of the same level
                            //hierarchy[i][2]: the index of the first child
                            //hierarchy[i][3]: the index of the parent
                            //
                            var listHollowArea = new List<LayerHollowArea>();
                            var hollowGroups = EmguContours.GetNegativeContoursInGroups(contours, hierarchy);
                            var areas = EmguContours.GetContoursArea(hollowGroups);

                            for (var i = 0; i < hollowGroups.Count; i++)
                            {
                                if (areas[i] < resinTrapConfig.RequiredAreaToProcessCheck) continue;
                                var rect = CvInvoke.BoundingRectangle(hollowGroups[i][0]);
                                listHollowArea.Add(new LayerHollowArea(hollowGroups[i].ToArrayOfArray(),
                                    rect,
                                    areas[i],
                                    layer.Index <= resinTrapConfig.StartLayerIndex ||
                                    layer.Index == LayerCount - 1 // First and Last layers, always drains
                                        ? LayerHollowArea.AreaType.Drain
                                        : LayerHollowArea.AreaType.Unknown));
                            }

                            if (listHollowArea.Count > 0) layerHollowAreas.TryAdd(layer.Index, listHollowArea);*/
                        }
                    }

                    progress.LockAndIncrement();
                }); // Parallel end
            }

            var matCache = new Mat[SlicerFile.LayerCount];
            var matTargetCache = new Mat[SlicerFile.LayerCount];
            void CacheLayers(uint layerIndex, bool direction)
            {
                if (matCache[layerIndex] is not null) return;
                int fromLayerIndex = (int)layerIndex;
                int toLayerIndex = (int)Math.Min(SlicerFile.LayerCount, layerIndex + Environment.ProcessorCount * 10 + 1);

                if (!direction)
                {
                    toLayerIndex = fromLayerIndex + 1;
                    fromLayerIndex = (int)Math.Max(resinTrapConfig.StartLayerIndex, fromLayerIndex - Environment.ProcessorCount * 10);
                }

                Parallel.For(fromLayerIndex, toLayerIndex,
                    i =>
                    {
                        matCache[i] = SlicerFile[i].LayerMat;
                        matTargetCache[i] = matCache[i].Roi(SlicerFile.BoundingRectangle);
                        if (resinTrapConfig.MaximumPixelBrightnessToDrain > 0)
                        {
                            CvInvoke.Threshold(matTargetCache[i], matTargetCache[i], resinTrapConfig.MaximumPixelBrightnessToDrain, byte.MaxValue, ThresholdType.Binary);
                        }
                    });
            }

            if (resinTrapConfig.Enabled)
            {
                //progress.Reset("Detecting Air Boundaries (Resin traps)", LayerCount);
                //if (progress.Token.IsCancellationRequested) return result.OrderBy(issue => issue.Type).ThenBy(issue => issue.LayerIndex).ThenBy(issue => issue.Area).ToList();
                progress.Reset("Detection pass 1 of 2 (Resin traps)", SlicerFile.LayerCount, resinTrapConfig.StartLayerIndex);

                /* define all mats up front, reducing allocations */
                var currentAirMap = EmguExtensions.InitMat(SlicerFile.BoundingRectangle.Size);
                var layerAirMap = currentAirMap.NewBlank();
                /* the first pass does bottom to top, and tracks anything it thinks is a resin trap */
                for (var layerIndex = resinTrapConfig.StartLayerIndex; layerIndex < SlicerFile.LayerCount; layerIndex++)
                {
                    if (progress.Token.IsCancellationRequested) return GetResult();

                    CacheLayers(layerIndex, true);
                    var curLayer = matTargetCache[layerIndex];

                    //curLayer.Save($"D:\\dump\\{layerIndex}_a.png");

                    /* find hollows of current layer */
                    GenerateAirMap(curLayer, layerAirMap, externalContours[layerIndex]);

                    //layerAirMap.Save($"D:\\dump\\{layerIndex}_b.png");

                    if (layerIndex == resinTrapConfig.StartLayerIndex)
                    {
                        currentAirMap = layerAirMap.Clone();
                    }

                    //currentAirMap.Save($"D:\\dump\\{layerIndex}_c.png");

                    /* remove solid areas of current layer from the air map */
                    CvInvoke.Subtract(currentAirMap, curLayer, currentAirMap);

                    //currentAirMap.Save($"D:\\dump\\{layerIndex}_d.png");

                    /* add in areas of air in current layer to air map */
                    CvInvoke.BitwiseOr(layerAirMap, currentAirMap, currentAirMap);

                    //currentAirMap.Save($"D:\\dump\\{layerIndex}_e.png");

                    if (hollows[layerIndex] is not null)
                    {
                        resinTraps[layerIndex] = new();
                        airContours[layerIndex] = new();
                        Parallel.For(0, hollows[layerIndex].Count, CoreSettings.ParallelOptions, i =>
                        {
                            //for (var i = 0; i < hollows[layerIndex].Count; i++)
                            //{
                            if (progress.Token.IsCancellationRequested) return;
                            if (resinTrapsContoursArea[layerIndex][i] < resinTrapConfig.RequiredAreaToProcessCheck) return;

                            /* intersect current contour, with the current airmap. */
                            using var currentContour = curLayer.NewBlank();
                            using var airOverlap = curLayer.NewBlank();
                            CvInvoke.DrawContours(currentContour, hollows[layerIndex][i], -1, EmguExtensions.WhiteColor, -1);
                            CvInvoke.BitwiseAnd(currentAirMap, currentContour, airOverlap);
                            var overlapCount = CvInvoke.CountNonZero(airOverlap);

                            lock (SlicerFile[layerIndex].Mutex)
                            {
                                if (overlapCount == 0)
                                {
                                    /* this countour does *not* overlap known air */

                                    /* add a resin trap (for now... will be revisited in part 2) */
                                    resinTraps[layerIndex].Add(hollows[layerIndex][i]);
                                }
                                else
                                {
                                    if (overlapCount >= resinTrapConfig.RequiredBlackPixelsToDrain)
                                    {
                                        /* this contour does overlap air, add it to the current air map and remember this contour was air-connected for 2nd pass */
                                        airContours[layerIndex].Add(hollows[layerIndex][i]);

                                        CvInvoke.BitwiseOr(currentContour, currentAirMap, currentAirMap);
                                    }
                                    else
                                    {
                                        /* it overlapped ,but not by enough, treat as solid */
                                        CvInvoke.Subtract(currentAirMap, currentContour, currentAirMap);
                                    }
                                }
                            }

                        });

                        if (progress.Token.IsCancellationRequested)
                            return GetResult();
                    }

                    matCache[layerIndex].Dispose();
                    matCache[layerIndex] = null;
                    matTargetCache[layerIndex] = null;

                    progress++;
                }

                if (progress.Token.IsCancellationRequested) return GetResult();
                progress.Reset("Detection pass 2 of 2 (Resin traps)", SlicerFile.LayerCount, resinTrapConfig.StartLayerIndex);
                /* starting over again but this time from the top to the bottom */
                if (currentAirMap is not null)
                {
                    currentAirMap.Dispose();
                    currentAirMap = null;
                }

                for (int layerIndex = resinTraps.Length-1; layerIndex >= resinTrapConfig.StartLayerIndex; layerIndex--)
                {
                    if (progress.Token.IsCancellationRequested) return GetResult();

                    CacheLayers((uint)layerIndex, false);
                    var curLayer = matTargetCache[layerIndex];

                    if (layerIndex == resinTraps.Length - 1)
                    {
                        /* this is subtly different that for the first pass, we don't use GenerateAirMap for the initial airmap */
                        /* instead we use a bitwise not, this way anything that is open/hollow on the top layer is treated as air */
                        currentAirMap = curLayer.Clone();
                        CvInvoke.BitwiseNot(curLayer, currentAirMap);
                    }

                    /* we still modify the airmap like normal, where we account for the air areas of the layer, and any contours that might overlap...*/
                    GenerateAirMap(curLayer, layerAirMap, externalContours[layerIndex]);

                    /* Update air map with any hollows that were found to be air-connected during first pass */
                    if (airContours[layerIndex] is not null)
                    {
                        Parallel.ForEach(airContours[layerIndex], CoreSettings.ParallelOptions, vec =>
                            CvInvoke.DrawContours(layerAirMap, vec, -1, EmguExtensions.WhiteColor, -1)
                        );
                    }

                    /* remove solid areas of current layer from the air map */
                    CvInvoke.Subtract(currentAirMap, curLayer, currentAirMap);

                    /* add in areas of air in current layer to air map */
                    CvInvoke.BitwiseOr(layerAirMap, currentAirMap, currentAirMap);

                    if (resinTraps[layerIndex] is not null)
                    {
                        suctionTraps[layerIndex] = new();
                        /* here we don't worry about finding contours on the layer, the bottom to top pass did that already */
                        /* all we care about is contours the first pass thought were resin traps, since there was no access to air from the bottom */
                        Parallel.For(0, resinTraps[layerIndex].Count, CoreSettings.ParallelOptions, x =>
                        {
                            if (progress.Token.IsCancellationRequested) return;

                            /* check if each contour overlaps known air */
                            using var currentContour = curLayer.NewBlank();
                            using var airOverlap = curLayer.NewBlank();
                            CvInvoke.DrawContours(currentContour, resinTraps[layerIndex][x], -1, EmguExtensions.WhiteColor, -1);

                            CvInvoke.BitwiseAnd(currentAirMap, currentContour, airOverlap);
                            var overlapCount = CvInvoke.CountNonZero(airOverlap);

                            lock (SlicerFile[layerIndex].Mutex)
                            {
                                if (overlapCount >= resinTrapConfig.RequiredBlackPixelsToDrain)
                                {
                                    /* this contour does overlap air, add this it our air map */
                                    CvInvoke.BitwiseOr(currentContour, currentAirMap, currentAirMap);
                                    /* Always add the removed contour to suctionTraps (even if we aren't reporting suction traps)
                                     * This is because contours that are placed on here get removed from resin traps in the next stage
                                     * if you don't put them here, they never get removed even if they should :) */

                                    /* if we haven't defined a suctionTrap list for this layer, do so */


                                    /* since we know it isn't a resin trap, it becomes a suction trap */
                                    suctionTraps[layerIndex].Add(resinTraps[layerIndex][x]);

                                    /* to keep things tidy while we iterate resin traps, it will be left in the list for now, and removed later */
                                }
                                else
                                {
                                    /* doesn't overlap by enough, remove from air map */
                                    CvInvoke.Subtract(currentAirMap, currentContour, currentAirMap);
                                }
                            }
                        });

                        /* anything that converted to a suction trap needs to removed from resinTraps. Loop backwards so indexes don't shift */
                        if (suctionTraps[layerIndex] is not null)
                        {
                            for (var i = suctionTraps[layerIndex].Count - 1; i >= 0; i--)
                            {
                                resinTraps[layerIndex].Remove(suctionTraps[layerIndex][i]);
                                if (resinTraps[layerIndex].Count > 0) continue;
                                resinTraps[layerIndex] = null;
                                break;
                            }

                        }
                    }

                    matCache[layerIndex].Dispose();
                    matCache[layerIndex] = null;
                    matTargetCache[layerIndex] = null;

                    progress++;
                }
                if (progress.Token.IsCancellationRequested) return GetResult();

                progress.Reset("Translate contours (Resin traps)", 0/*(uint)(resinTraps.Length + suctionTraps.Length)*/);

                /* translate all contour points by ROI x and y */
                var offsetBy = new Point(SlicerFile.BoundingRectangle.X, SlicerFile.BoundingRectangle.Y);
                foreach (var listOfLayers in new[] { resinTraps, suctionTraps })
                {
                    Parallel.ForEach(listOfLayers, contoursGroups =>
                    {
                        if (contoursGroups is null) return;
                        for (var groupIndex = 0; groupIndex < contoursGroups.Count; groupIndex++)
                        {
                            var contours = contoursGroups[groupIndex];
                            if (contours is null) continue;

                            var arrayOfArrayOfPoints = contours.ToArrayOfArray();

                            foreach (var pointArray in arrayOfArrayOfPoints)
                                for (var i = 0; i < pointArray.Length; i++)
                                    pointArray[i].Offset(offsetBy);

                            contoursGroups[groupIndex].Dispose();
                            contoursGroups[groupIndex] = new VectorOfVectorOfPoint(arrayOfArrayOfPoints);
                        }

                        //progress.LockAndIncrement();
                    });
                }

                if (progress.Token.IsCancellationRequested) return GetResult();

                for (var layerIndex = 0; layerIndex < resinTraps.Length; layerIndex++)
                {
                    if (resinTraps[layerIndex] == null) continue;

                    /* select new LayerIssue(this[layerIndex], LayerIssue.IssueType.ResinTrap, area.Contour, area.BoundingRectangle)) */
                    foreach (var trap in resinTraps[layerIndex])
                    {
                        var area = EmguContours.GetContourArea(trap);
                        var rect = CvInvoke.BoundingRectangle(trap[0]);
                        AddIssue(new MainIssue(MainIssue.IssueType.ResinTrap, new IssueOfContours(SlicerFile[layerIndex], trap.ToArrayOfArray(), rect, area)));
                    }
                }

                /* only report suction cup issues if enabled */
                if (resinTrapConfig.DetectSuctionCups)
                {
                    var minimumSuctionArea = resinTrapConfig.RequiredAreaToConsiderSuctionCup;
                    for (var layerIndex = suctionTraps.Length - 1; layerIndex >= 0; layerIndex--)
                    {
                        if (suctionTraps[layerIndex] == null) continue;

                        foreach (var trap in suctionTraps[layerIndex])
                        {
                            var area = EmguContours.GetContourArea(trap);
                            if (area < minimumSuctionArea) continue;
                            var rect = CvInvoke.BoundingRectangle(trap[0]);
                            AddIssue(new MainIssue(MainIssue.IssueType.SuctionCup, new IssueOfContours(SlicerFile[layerIndex], trap.ToArrayOfArray(), rect, area)));
                        }
                    }
                }

                // Dispose
                foreach (var listOfVectors in new[] { resinTraps, suctionTraps, hollows, airContours })
                {
                    foreach (var vectorArray in listOfVectors)
                    {
                        if (vectorArray is null) continue;
                        foreach (var vector in vectorArray)
                        {
                            vector?.Dispose();
                        }
                    }
                }

                foreach (var vector in externalContours)
                {
                    vector?.Dispose();
                }

            }

            return GetResult();
        }
    }
}
