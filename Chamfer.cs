﻿using ActiproSoftware.Windows.Controls;
using ActiproSoftware.Windows.Extensions;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Data.UtilityNetwork.Trace;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Editing.Attributes;
using ArcGIS.Desktop.Extensions;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Internal.Core;
using ArcGIS.Desktop.Internal.Core.Behaviors;
using ArcGIS.Desktop.Internal.Core.History;
using ArcGIS.Desktop.KnowledgeGraph;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using static System.Formats.Asn1.AsnWriter;
using Geometry = ArcGIS.Core.Geometry.Geometry;
using LineSegment = ArcGIS.Core.Geometry.LineSegment;

namespace Chamfer
{
    internal class Chamfer : MapTool
    {
        enum TrackingState
        {
            NotTracking = 0,
            CanTrack,
            Tracking
        }

        // TODO: Implement two graphic layers to show selected lines and preview simultaneously
        private IDisposable _graphic = null;
        private CIMLineSymbol _solid_line = null;
        private CIMLineSymbol _dashed_line = null;

        // List < (InfiniteLine bases on selected segment, feature layer name as string) >
        private List< (InfiniteLine, string) > _selected_segments = new();

        private System.Windows.Point? _lastLocation = null;
        private System.Windows.Point? _workingLocation = null;

        private TrackingState _trackingMouseMove = TrackingState.NotTracking;
        private static readonly object _lock = new object();

        public class InfiniteLine
        // Contains a slope-intercept defenition of a two-point line, as well as the endpoints used to construct said line
        // Intended to operate on two-point line segments
        // TODO: Implement double.PositiveInfinity rather than null railroad
        // TODO: Throw error for line with more than two points
        // TODO: Throw error for mismatched SpatialReference between points
        // TODO: Get endpoint tangent for arcs?
        {
            public readonly Polyline Polyline;
            public readonly Segment Segment;
            public readonly MapPoint StartPoint;
            public readonly MapPoint EndPoint;
            public readonly SpatialReference SpatialReference;
            public readonly double? Slope;
            public readonly double Intercept;
            public string FeatureLayerName = null;


            public InfiniteLine(Polyline line) : this(line.Points[0], line.Points[^1], line) { }
            public InfiniteLine(Segment seg) : this(seg.StartPoint, seg.EndPoint, null, seg) { }
            public InfiniteLine(MapPoint start_point, MapPoint end_point, Polyline display_geometry = null, Segment segment = null)
            {
                SpatialReference = start_point.SpatialReference;
                Polyline = (display_geometry == null)
                    ? PolylineBuilderEx.CreatePolyline(new[] { start_point, end_point }, SpatialReference)
                    : display_geometry;
                Segment = (segment == null)
                    ? Polyline.Parts.FirstOrDefault().FirstOrDefault()
                    : segment;
                StartPoint = start_point;
                EndPoint = end_point;
                Slope = (end_point.Y - start_point.Y) / (end_point.X - start_point.X);
                Intercept = start_point.Y - (Slope.Value * start_point.X);
            }
        }

        public Chamfer()
        {
            IsSketchTool = true;
            SketchType = SketchGeometryType.Point;
            SketchOutputMode = SketchOutputMode.Map;
            UseSnapping = true;
        }

        protected override async Task OnToolActivateAsync(bool active)
        {
            _lastLocation = null;
            _workingLocation = null;
            _trackingMouseMove = TrackingState.NotTracking;
            if (_solid_line == null || _dashed_line == null)
            {
                await QueuedTask.Run(() =>
                {
                    _solid_line = SymbolFactory.Instance.ConstructLineSymbol(CIMColor.CreateRGBColor(128, 128, 128), 3.0, SimpleLineStyle.Solid);
                    _dashed_line = SymbolFactory.Instance.ConstructLineSymbol(CIMColor.CreateRGBColor(0, 0, 0), 3.0, SimpleLineStyle.Dash);
                });
            }
        }

        protected override Task<bool> OnSketchCompleteAsync(Geometry point_selection)
        {
            QueuedTask.Run(() =>
            {
                IGeometryEngine geo = GeometryEngine.Instance;

                // Remove if we cannot find a way to make this work
                //SelectionSet selected_features = MapView.Active.GetFeaturesEx(point_selection, false, false); // Get visually selected features from active map
                //MapView.Active.FlashFeature(selected_features);

                IEnumerable<FeatureLayer> all_features = MapView.Active.Map.GetMapMembersAsFlattenedList().OfType<FeatureLayer>();

                // This spatial relationship seems to miss many feature classes in houston template
                // TODO: Methodically test this
                var spatial_filter = new SpatialQueryFilter()
                {
                    FilterGeometry = point_selection,
                    SpatialRelationship = SpatialRelationship.EnvelopeIntersects
                };

                var insp = new Inspector();
                List<(InfiniteLine, string)> potential_selected_segments = new();
                String test_str = "";
                foreach (FeatureLayer feature_layer in all_features)
                {
                    FeatureClass feature_class = feature_layer.GetFeatureClass();

                    test_str += "--------\n" + (feature_layer.Name) + "\n";

                    // Skip layers not visible
                    if (!feature_layer.IsVisible)
                        continue;

                    RowCursor cursor = feature_layer.Search(spatial_filter);
                    if (cursor == null)
                        continue;
                    test_str += "Cursor not null" + "\n";
                    while (cursor.MoveNext())
                    {
                        //MessageBox.Show(feature_layer.ToString());
                        using (Row row = cursor.Current)
                        {
                            test_str += "In cursor" + "\n";
                            insp.Load(row);

                            // Skip annotations
                            // TODO: Look into a way to test for this before iteration
                            if (insp.HasAnnotationAttributes)
                                break;

                            // TODO: Test for shapetype before iteration
                            Polyline shape_as_polyline = insp.Shape.GeometryType == GeometryType.Polygon
                                ? geo.Boundary(insp.Shape) as Polyline
                                : insp.Shape as Polyline;
                            
                            // Skip non-polyline or null geometry
                            if (shape_as_polyline == null)
                                continue;

                            //  ProjectionTransformation map_transormation = ArcGIS.Core.Geometry.ProjectionTransformation.Create(insp.Shape.SpatialReference, MapView.Active.Map.SpatialReference);
                            Polyline projected_line = geo.Project(shape_as_polyline, point_selection.SpatialReference) as Polyline;
                            if ( geo.Distance(point_selection, projected_line) > .1)
                                continue;

                            // Add all possible selected segments to a list
                            potential_selected_segments.AddRange(
                                projected_line.Parts
                                .SelectMany(segment => segment)
                                .Select( segment => ( new InfiniteLine(segment), feature_layer.Name.ToString() ) ).ToList()
                            );
                        }
                    }
                }

                // Debug output; Remove
                //ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(test_str);

                (InfiniteLine, string) closest_segment = potential_selected_segments
                    .OrderBy(item => geo.Distance(point_selection, item.Item1.Polyline))
                    .FirstOrDefault();

                if (closest_segment.Item1 == null)
                    return false;

                //InfiniteLine selected_line = closest_segment.Item1;

                // Case: No prior selection
                if (_selected_segments.Count == 0)
                {
                    _selected_segments.Clear();
                    _selected_segments.Add(closest_segment);
                    lock (_lock)
                    {
                        if (_graphic != null)
                            _graphic.Dispose();
                        _graphic = this.AddOverlay(closest_segment.Item1.Polyline, _solid_line.MakeSymbolReference());
                    }
                    _trackingMouseMove = TrackingState.NotTracking;
                }
                // Case: One segment already selected
                else if (_selected_segments.Count == 1)
                {
                    Polyline chamfer_geometry = ChamferLines(_selected_segments[0].Item1, closest_segment.Item1, point_selection as MapPoint);
                    // Case: No intersection found (parallel lines)
                    if (chamfer_geometry == null)
                        return false;
                    _selected_segments.Add(closest_segment);
                    lock (_lock)
                    {
                        this.UpdateOverlay(_graphic, chamfer_geometry, _dashed_line.MakeSymbolReference());
                    }
                    _trackingMouseMove = TrackingState.CanTrack;
                }
                // Case: Two segments already selected
                // This is a stub to allow repeated testing of initial selection
                else if (_selected_segments.Count > 1)
                {
                    QueuedTask.Run(() =>
                    {
                        FeatureLayer layer1 = MapView.Active.Map
                            .GetMapMembersAsFlattenedList().OfType<FeatureLayer>()
                            .Where(layer => layer.Name == _selected_segments[0].Item2)
                            .FirstOrDefault();

                        FeatureLayer layer2 = MapView.Active.Map
                            .GetMapMembersAsFlattenedList().OfType<FeatureLayer>()
                            .Where(layer => layer.Name == _selected_segments[1].Item2)
                            .FirstOrDefault();
                        // This spatial relationship seems to miss many feature classes in houston template
                        // TODO: Methodically test this
                        var spatial_filter = new SpatialQueryFilter()
                        {
                            FilterGeometry = point_selection,
                            SpatialRelationship = SpatialRelationship.EnvelopeIntersects
                        };
                    });

                    _selected_segments.Clear();
                    lock (_lock)
                    {
                        if (_graphic != null)
                            _graphic.Dispose();
                    }
                    _trackingMouseMove = TrackingState.NotTracking;
                }
                // Edit operation syntax
                //var op = new EditOperation()
                //{
                //    Name = "Test Operation",
                //    SelectModifiedFeatures = true,
                //    SelectNewFeatures = false
                //};
                //op.Modify(insp);
                //return op.Execute();
                return true;
            });
            return Task.FromResult(true);
        }

        protected override Task<bool> OnToolDeactivateAsync(bool hasMapViewChanged)
        {
            _solid_line = null;
            _dashed_line = null;
            _selected_segments = new();

            lock (_lock)
            {
                if (_graphic != null)
                {
                    _graphic.Dispose();
                    _graphic = null;
                }
            }

            return Task.FromResult(true);
        }

        // TODO: Implement esc handling to further mimic fillet behavior
        protected override async void OnToolMouseMove(MapViewMouseEventArgs e)
        {
            //All of this logic is to avoid unnecessarily updating the graphic position
            //for ~every~ mouse move. We skip any "intermediate" points in-between rapid
            //mouse moves.
            lock (_lock)
            {
                if (_trackingMouseMove == TrackingState.NotTracking)
                    return;
                else
                {
                    if (_workingLocation.HasValue)
                    {
                        _lastLocation = e.ClientPoint;
                        return;
                    }
                    else
                    {
                        _lastLocation = e.ClientPoint;
                        _workingLocation = e.ClientPoint;
                    }
                }
                _trackingMouseMove = TrackingState.Tracking;
            }
            //The code "inside" the QTR will execute for all points that
            //get "buffered" or "queued". This avoids having to spin up a QTR
            //for ~every~ point of ~every mouse move.

            await QueuedTask.Run(() =>
            {
                while (true)
                {
                    System.Windows.Point? point;
                    IDisposable graphic = null;
                    MapPoint mouse_point = null;
                    lock (_lock)
                    {
                        point = _lastLocation;
                        _lastLocation = null;
                        _workingLocation = point;
                        if (point == null || !point.HasValue)
                        {
                            //No new points came in while we updated the overlay
                            _workingLocation = null;
                            break;
                        }
                        else if (_graphic == null)
                        {
                            //conflict with the mouse down,
                            //If this happens then we are done. A new line and point will be
                            //forthcoming from the SketchCompleted callback
                            _trackingMouseMove = TrackingState.NotTracking;
                            break;
                        }
                        graphic = _graphic;
                        if (point.HasValue)
                            mouse_point = this.ActiveMapView.ClientToMap(point.Value);
                    }
                    if (mouse_point != null)
                    {
                        //update the graphic overlay

                        Polyline preview_line = ChamferLines(_selected_segments[0].Item1, _selected_segments[1].Item1, mouse_point);

                        this.UpdateOverlay(graphic, preview_line, _dashed_line.MakeSymbolReference());
                    }
                }
            });
        }

        #region Internal Functions

        // Find theoretical intersection point between two segments (assumes straight lines)
        // TODO: add case for curved segments (tangent line @ endpoint?) (QueryTangent?)
        private static MapPoint GetIntersectionPoint(InfiniteLine line1, InfiniteLine line2)
        {
            // TODO: Add case for existing intersection point
            if (line1.SpatialReference != line2.SpatialReference)
                return null;
            double int_x;
            double int_y;
            // Case: Parallel lines (also catches parallel vertical lines)
            if (line1.Slope == line2.Slope)
                return null;
            else
            {
                int_x = (line2.Intercept - line1.Intercept) / (line1.Slope.Value - line2.Slope.Value);
                int_y = (line1.Slope.Value * int_x) + line1.Intercept;
            }

            MapPoint int_point = MapPointBuilderEx.CreateMapPoint(int_x, int_y, line1.SpatialReference);

            return int_point;
        }

        private static double DistanceInDirection(InfiniteLine vector, MapPoint point)
        {
            // Original vector (vector.EndPoint.X, vector.EndPoint.Y) -> (vector.StartPoint.X, vector.StartPoint.Y)

            // Calculate the angle θ in radians
            double dx = vector.StartPoint.X - vector.EndPoint.X;
            double dy = vector.StartPoint.Y - vector.EndPoint.Y;
            double theta = Math.Atan2(dy, dx);

            // Translate the point to the vector's starting position
            double translatedX = point.X - vector.EndPoint.X;
            double translatedY = point.Y - vector.EndPoint.Y;

            // Apply the rotation matrix
            double cosTheta = Math.Cos(theta);
            double sinTheta = Math.Sin(theta);
            double rotatedX = translatedX * cosTheta + translatedY * sinTheta;
            double rotatedY = -translatedX * sinTheta + translatedY * cosTheta;

            // Translate back if needed
            double finalX = rotatedX + vector.EndPoint.X;
            double finalY = rotatedY + vector.EndPoint.Y;

            return finalX;
        }

        private static Polyline ChamferLines(InfiniteLine line1, InfiniteLine line2, MapPoint mouse_point = null)
        {
            IGeometryEngine geo = GeometryEngine.Instance;
            MapPoint intersection_point = GetIntersectionPoint(line1, line2);
            if (intersection_point == null) // This will filter out parallel lines, including two with null slope
                return null;
            if (mouse_point == null)
                return null;
            var lines = new[] { line1, line2 };

            double shortest_distance = lines.Min(line => line.Segment.Length);
            List<LeftOrRightSide> sides = new();
            List<MapPoint> chamfer_ratio_points = new();
            foreach (InfiniteLine line in lines)
            {
                // Find point farthest 
                MapPoint farthest_point = new[] { line.StartPoint, line.EndPoint }
                    .OrderByDescending(point => geo.Distance(intersection_point, point))
                    .FirstOrDefault();
                LineSegment intersection_segment = LineBuilderEx.CreateLineSegment(intersection_point, farthest_point, intersection_point.SpatialReference);

                LeftOrRightSide side;
                GeometryEngine.Instance.QueryPointAndDistance(intersection_segment, SegmentExtensionType.ExtendTangents, mouse_point, AsRatioOrLength.AsRatio, out _, out _, out side);
                sides.Add(side);

                double intersection_angle = intersection_segment.Angle;
                chamfer_ratio_points.Add(geo.ConstructPointFromAngleDistance(intersection_point, intersection_angle, shortest_distance, line.SpatialReference));
            }

            double chamfer_angle = LineBuilderEx.CreateLineSegment(chamfer_ratio_points[0], chamfer_ratio_points[1], line1.SpatialReference).Angle;

            if (sides[0] == sides[1])
                chamfer_angle += (Math.PI / 2);

            MapPoint mouse_chamfer_point = geo.ConstructPointFromAngleDistance(mouse_point, chamfer_angle, shortest_distance, mouse_point.SpatialReference);

            InfiniteLine chamfer_stub_line = new(mouse_point, mouse_chamfer_point);
            InfiniteLine mouse_line = new(mouse_point, intersection_point);

            var int_pt1 = GetIntersectionPoint(line1, chamfer_stub_line);
            MapPoint end_pt1 = ( DistanceInDirection(mouse_line, line1.Segment.StartPoint) > DistanceInDirection(mouse_line, line1.Segment.EndPoint) )
                ? line1.Segment.StartPoint
                : line1.Segment.EndPoint;

            var int_pt2 = GetIntersectionPoint(line2, chamfer_stub_line);
            MapPoint end_pt2 = (DistanceInDirection(mouse_line, line2.Segment.StartPoint) > DistanceInDirection(mouse_line, line2.Segment.EndPoint))
                ? line2.Segment.StartPoint
                : line2.Segment.EndPoint;

            Polyline chamfer_line = PolylineBuilderEx.CreatePolyline(new[] { int_pt1, int_pt2 }, line1.SpatialReference);
            Polyline line1_connection = PolylineBuilderEx.CreatePolyline(new[] { end_pt1, int_pt1 }, line1.SpatialReference);
            Polyline line2_connection = PolylineBuilderEx.CreatePolyline(new[] { end_pt2, int_pt2 }, line2.SpatialReference);

            return geo.Union(new[] { line1_connection, chamfer_line, line2_connection }) as Polyline;

            // Use this to determine quadrant, based on intersection - endpoint lines
            // If in outer two quadrants, rotate chamfer angle 90 degrees
            //GeometryEngine.Instance.QueryPointAndDistance(line.Polyline, SegmentExtensionType.ExtendTangents, mouse_point, AsRatioOrLength.AsRatio, out _, out _, out side);
        }

        #endregion
    }
}
