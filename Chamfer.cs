using ActiproSoftware.Windows.Controls;
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
using System.Windows.Media.Animation;
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
        private IDisposable _selection_graphic = null;
        private IDisposable _preview_graphic = null;
        private CIMLineSymbol _solid_line = null;
        private CIMLineSymbol _dashed_line = null;

        // List < (InfiniteLine bases on selected segment, feature layer name as string) >
        private List<InfiniteLine> _selected_segments = new();

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
            public readonly string FeatureLayerName = null;
            public readonly long? OID = null;

            public InfiniteLine(Polyline line, string featureLayerName = null, long? objectID = null) : this(line.Points[0], line.Points[^1], line, null, featureLayerName, objectID) { }
            public InfiniteLine(Segment seg) : this(seg.StartPoint, seg.EndPoint, null, seg) { }
            public InfiniteLine(Segment seg, string fl_name, long oid) : this(seg.StartPoint, seg.EndPoint, null, seg, fl_name, oid) { }
            public InfiniteLine(MapPoint start_point, MapPoint end_point, Polyline polyline = null, Segment segment = null, string featureLayerName = null, long? objectID = null)
            {
                SpatialReference = start_point.SpatialReference;
                Polyline = (polyline == null)
                    ? PolylineBuilderEx.CreatePolyline(new[] { start_point, end_point }, SpatialReference)
                    : polyline;
                Segment = (segment == null)
                    ? Polyline.Parts.FirstOrDefault().FirstOrDefault()
                    : segment;
                StartPoint = start_point;
                EndPoint = end_point;
                Slope = (end_point.Y - start_point.Y) / (end_point.X - start_point.X);
                Intercept = start_point.Y - (Slope.Value * start_point.X);

                FeatureLayerName = featureLayerName;
                OID = objectID;
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
                var insp = new Inspector();

                // Run and skip other logic if two segments are already selected
                if (_selected_segments.Count > 1)
                {
                    _trackingMouseMove = TrackingState.NotTracking;
                    var op = new EditOperation()
                    {
                        Name = "Chamfer",
                        SelectModifiedFeatures = true,
                        SelectNewFeatures = true
                    };
                    ChamferLines(_selected_segments[0], _selected_segments[1], point_selection as MapPoint, out Polyline chamfer_line, out Polyline new_line1, out Polyline new_line2);

                    var iterList = (_selected_segments[0].OID == _selected_segments[1].OID)
                        ? new[] { (
                            new InfiniteLine(
                                geo.Union(_selected_segments[0].Polyline, _selected_segments[1].Polyline) as Polyline,
                                _selected_segments[0].FeatureLayerName,
                                _selected_segments[1].OID
                            ),
                            geo.Union(new_line1, new_line2) as Polyline
                        ) }
                        : new[] { (_selected_segments[0], new_line1), (_selected_segments[1], new_line2) };

                    foreach ( (InfiniteLine old_line, Polyline new_line) in iterList )
                    {
                        FeatureLayer feature_layer = MapView.Active.Map
                            .GetMapMembersAsFlattenedList().OfType<FeatureLayer>()
                            .Where(layer => layer.Name == old_line.FeatureLayerName)
                            .FirstOrDefault();
                        FeatureClass feature_class = feature_layer.GetFeatureClass();

                        var OIDFilter = new QueryFilter()
                        {
                            WhereClause = $"OBJECTID = {old_line.OID}"
                        };

                        RowCursor cursor = feature_class.Search(OIDFilter);
                        if (cursor == null)
                            continue;

                        while (cursor.MoveNext())
                        {
                            using (Row row = cursor.Current)
                            {
                                insp.Load(row);

                                // TODO: Test if newline overlaps old line before running difference
                                insp.Shape = geo.Difference(insp.Shape, old_line.Polyline);
                                insp.Shape = geo.Union(insp.Shape, new_line);
                                op.Modify(insp);
                            }
                        }
                    }
                    op.Execute();

                    //ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(test_str);

                    _selected_segments.Clear();
                    lock (_lock)
                    {
                        if (_selection_graphic != null)
                            _selection_graphic.Dispose();
                        if (_preview_graphic != null)
                            _preview_graphic.Dispose();
                    }

                    return true;
                }

                IEnumerable<FeatureLayer> all_features = MapView.Active.Map.GetMapMembersAsFlattenedList().OfType<FeatureLayer>();

                // SpatialRelationship.Intersect does not seem to have consistent behavior; use intersection with tolorance after ElvelopeIntersect filter
                var spatial_filter = new SpatialQueryFilter()
                {
                    FilterGeometry = point_selection,
                    SpatialRelationship = SpatialRelationship.EnvelopeIntersects
                };

                List<InfiniteLine> potential_selected_segments = new();
                foreach (FeatureLayer feature_layer in all_features)
                {
                    FeatureClass feature_class = feature_layer.GetFeatureClass();

                    // Skip layers not visible
                    if (!feature_layer.IsVisible)
                        continue;

                    RowCursor cursor = feature_layer.Search(spatial_filter);
                    if (cursor == null)
                        continue;
                    while (cursor.MoveNext())
                    {
                        using (Row row = cursor.Current)
                        {
                            insp.Load(row);

                            if (insp.HasAnnotationAttributes)
                                break;
                            
                            Polyline shape_as_polyline = (insp.Shape.GeometryType == GeometryType.Polygon)
                                ? geo.Boundary(insp.Shape) as Polyline
                                : insp.Shape as Polyline;
                            
                            // Skip non-polyline or null geometry
                            if (shape_as_polyline == null)
                                continue;
                            
                            // Skip if line is not within tolorance of selection point
                            Polyline projected_line = geo.Project(shape_as_polyline, point_selection.SpatialReference) as Polyline;
                            if ( geo.Distance(point_selection, projected_line) > .1)
                                continue;

                            // Add all possible selected segments to a list
                            potential_selected_segments.AddRange(
                                shape_as_polyline.Parts
                                .SelectMany(segment => segment)
                                .Select( segment => new InfiniteLine(segment, feature_layer.Name.ToString(), row.GetObjectID()) )
                            );
                        }
                    }
                }

                InfiniteLine closest_segment  = potential_selected_segments
                    .OrderBy( seg => geo.Distance(geo.Project(point_selection, seg.SpatialReference), seg.Polyline) )
                    .FirstOrDefault();

                if (closest_segment == null)
                    return false;
                
                // Case: No prior selection
                if (_selected_segments.Count == 0)
                {
                    _selected_segments.Clear();
                    _selected_segments.Add(closest_segment);
                    lock (_lock)
                    {
                        if (_selection_graphic != null)
                            _selection_graphic.Dispose();
                        if (_preview_graphic != null)
                            _preview_graphic.Dispose();
                        _selection_graphic = AddOverlay(closest_segment.Polyline, _solid_line.MakeSymbolReference());
                    }
                    _trackingMouseMove = TrackingState.NotTracking;
                }

                // Case: One segment already selected
                else if (_selected_segments.Count == 1)
                {
                    ChamferLines(_selected_segments[0], closest_segment, point_selection as MapPoint, out Polyline chamfer_geometry, out _, out _);
                    // No intersection found (parallel lines or mismatched spatial reference)
                    if (chamfer_geometry == null)
                        return false;
                    _selected_segments.Add(closest_segment);
                    lock (_lock)
                    {
                        UpdateOverlay(_selection_graphic, geo.Union(_selected_segments[0].Polyline, _selected_segments[1].Polyline), _solid_line.MakeSymbolReference());
                        _preview_graphic = AddOverlay(chamfer_geometry, _dashed_line.MakeSymbolReference());
                    }
                    _trackingMouseMove = TrackingState.CanTrack;
                }
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
                if (_selection_graphic != null)
                {
                    _selection_graphic.Dispose();
                    _selection_graphic = null;
                }
                if (_preview_graphic != null)
                {
                    _preview_graphic.Dispose();
                    _preview_graphic = null;
                }
            }

            return Task.FromResult(true);
        }

        // TODO: Implement esc handling to further mimic fillet behavior
        // This mouse tracking behavior comes from the ESRI community examples
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
                        else if (_preview_graphic == null)
                        {
                            //conflict with the mouse down,
                            //If this happens then we are done. A new line and point will be
                            //forthcoming from the SketchCompleted callback
                            _trackingMouseMove = TrackingState.NotTracking;
                            break;
                        }
                        graphic = _preview_graphic;
                        if (point.HasValue)
                            mouse_point = this.ActiveMapView.ClientToMap(point.Value);
                    }
                    if (mouse_point != null)
                    {
                        //update the graphic overlay

                        ChamferLines(_selected_segments[0], _selected_segments[1], mouse_point, out Polyline preview_line, out _, out _);

                        UpdateOverlay(graphic, preview_line, _dashed_line.MakeSymbolReference());
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
                //ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show("SR");
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

        private static void ChamferLines(InfiniteLine line1, InfiniteLine line2, MapPoint mouse_point, out Polyline chamfer_line, out Polyline line1_connection, out Polyline line2_connection)
        {
            chamfer_line = null;
            line1_connection = null;
            line2_connection = null;
            if (line1.SpatialReference != line2.SpatialReference)
                return;
            if (mouse_point == null)
                return;
            IGeometryEngine geo = GeometryEngine.Instance;
            mouse_point = geo.Project(mouse_point, line1.SpatialReference) as MapPoint;

            MapPoint intersection_point = GetIntersectionPoint(line1, line2);
            if (intersection_point == null) // This will filter out parallel lines, including two with null slope
                return;
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

            chamfer_line = PolylineBuilderEx.CreatePolyline(new[] { int_pt1, int_pt2 }, line1.SpatialReference);
            line1_connection = PolylineBuilderEx.CreatePolyline(new[] { end_pt1, int_pt1, int_pt2 }, line1.SpatialReference);
            //line1_connection = geo.Union(chamfer_line, line1_connection) as Polyline;
            line2_connection = PolylineBuilderEx.CreatePolyline(new[] { end_pt2, int_pt2 }, line1.SpatialReference);
        }

        #endregion
    }
}
