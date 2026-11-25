using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// Maps the rendered programme (and the test pattern on the same stage) through a real mesh. A
/// rectangular transform can only scale, rotate and move the picture; this projects it onto a
/// grid whose corners and edges can be bowed or pinned independently, which is what a processor
/// needs for curved LED walls, angled projectors and irregular surfaces.
/// </summary>
public sealed class NodeProjection : Grid
{
    const int Columns = 32;
    const int Rows = 18;
    const int MaxCaptureWidth = 1920;
    const int MaxCaptureHeight = 1080;

    readonly FrameworkElement _source;
    readonly Viewport3D _viewport = new() { ClipToBounds = true, IsHitTestVisible = false };
    readonly MeshGeometry3D _mesh = new();
    readonly ImageBrush _texture = new() { Stretch = Stretch.Fill };
    readonly GeometryModel3D _model;
    NodeCalibration _calibration = new();
    int _outputWidth = 1920;
    int _outputHeight = 1080;
    int _captureWidth;
    int _captureHeight;

    public NodeProjection(FrameworkElement source)
    {
        _source = source;
        Background = Brushes.Black;
        ClipToBounds = true;
        var material = new DiffuseMaterial(_texture);
        // The mesh is viewed from the camera side; BackMaterial makes the mapping robust to the
        // triangle winding used by the generated curved grid (and to a mirrored output).
        _model = new GeometryModel3D(_mesh, material) { BackMaterial = material };
        var camera = new OrthographicCamera
        {
            Position = new Point3D(0, 0, 10),
            LookDirection = new Vector3D(0, 0, -1),
            UpDirection = new Vector3D(0, 1, 0),
            Width = 1
        };
        _viewport.Camera = camera;
        _viewport.Children.Add(new ModelVisual3D { Content = new AmbientLight(Colors.White) });
        _viewport.Children.Add(new ModelVisual3D { Content = _model });
        Children.Add(_viewport);
        BuildMesh();
    }

    public void Configure(NodeCalibration calibration, int width, int height)
    {
        _calibration = calibration.Copy();
        _outputWidth = Math.Max(1, width);
        _outputHeight = Math.Max(1, height);
        BuildMesh();
    }

    /// <summary>Captures the live WPF stage into the mesh texture. Called at a bounded rate by the
    /// node window; the source visual keeps its normal animations and clocks.</summary>
    public void Refresh()
    {
        if (!IsLoaded || _source.ActualWidth < 1 || _source.ActualHeight < 1) return;
        var scale = Math.Min(1.0, Math.Min(MaxCaptureWidth / _source.ActualWidth, MaxCaptureHeight / _source.ActualHeight));
        var width = Math.Max(1, (int)Math.Round(_source.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Round(_source.ActualHeight * scale));
        if (width != _captureWidth || height != _captureHeight)
        {
            _captureWidth = width;
            _captureHeight = height;
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_source);
        _texture.ImageSource = bitmap;
    }

    void BuildMesh()
    {
        var positions = new Point3DCollection((Columns + 1) * (Rows + 1));
        var texture = new PointCollection((Columns + 1) * (Rows + 1));
        var indices = new Int32Collection(Columns * Rows * 6);
        var radians = _calibration.RotationDegrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var scaleX = Math.Clamp(_calibration.ScaleX, 0.05, 4);
        var scaleY = Math.Clamp(_calibration.ScaleY, 0.05, 4);
        var width = _outputWidth;
        var height = _outputHeight;

        for (var row = 0; row <= Rows; row++)
        {
            var v = row / (double)Rows;
            for (var column = 0; column <= Columns; column++)
            {
                var u = column / (double)Columns;
                // Bilinear corner pinning. Corner values are in output pixels and the same edge
                // interpolation is used for every vertex, so an irregular quadrilateral stays
                // continuous instead of showing four separate rectangular crops.
                var topLeft = (1 - u) * (1 - v);
                var topRight = u * (1 - v);
                var bottomLeft = (1 - u) * v;
                var bottomRight = u * v;
                var cornerX = topLeft * _calibration.CornerTopLeftX
                    + topRight * _calibration.CornerTopRightX
                    + bottomLeft * _calibration.CornerBottomLeftX
                    + bottomRight * _calibration.CornerBottomRightX;
                var cornerY = topLeft * _calibration.CornerTopLeftY
                    + topRight * _calibration.CornerTopRightY
                    + bottomLeft * _calibration.CornerBottomLeftY
                    + bottomRight * _calibration.CornerBottomRightY;

                var x = (u - 0.5) * scaleX + cornerX / width;
                var y = (0.5 - v) * scaleY - cornerY / height;
                // A curved surface bows horizontally across its height and vertically across its
                // width. The sine profile is zero at the ends and largest in the middle.
                x += _calibration.CurveX / width * Math.Sin(Math.PI * v);
                y -= _calibration.CurveY / height * Math.Sin(Math.PI * u);

                var rotatedX = x * cos - y * sin;
                var rotatedY = x * sin + y * cos;
                x = rotatedX + _calibration.OffsetX / width;
                y = rotatedY - _calibration.OffsetY / height;

                positions.Add(new Point3D(x, y, 0));
                texture.Add(new Point(u, v));
            }
        }

        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var topLeft = row * (Columns + 1) + column;
                var topRight = topLeft + 1;
                var bottomLeft = topLeft + Columns + 1;
                var bottomRight = bottomLeft + 1;
                indices.Add(topLeft);
                indices.Add(topRight);
                indices.Add(bottomRight);
                indices.Add(topLeft);
                indices.Add(bottomRight);
                indices.Add(bottomLeft);
            }
        }

        _mesh.Positions = positions;
        _mesh.TextureCoordinates = texture;
        _mesh.TriangleIndices = indices;
    }
}
