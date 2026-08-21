using Avalonia;
using QrOverlayScanner.Models;
using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;

namespace QrOverlayScanner.Services;

public sealed class QrDecoder
{
    private readonly BarcodeReaderGeneric _reader = new()
    {
        AutoRotate = false,
        Options = new DecodingOptions
        {
            TryHarder = true,
            TryInverted = true,
            PossibleFormats = [BarcodeFormat.QR_CODE]
        }
    };

    public IReadOnlyList<QrDetection> Decode(ScreenFrame frame)
    {
        var results = _reader.DecodeMultiple(
            frame.BgraPixels,
            frame.Width,
            frame.Height,
            RGBLuminanceSource.BitmapFormat.BGRA32);

        if (results is null || results.Length == 0)
            return [];

        var detections = new List<QrDetection>(results.Length);
        foreach (var result in results)
        {
            if (result.BarcodeFormat != BarcodeFormat.QR_CODE ||
                string.IsNullOrWhiteSpace(result.Text) ||
                !TryCalculateCompleteBounds(result, frame.Width, frame.Height, out var bounds))
            {
                continue;
            }

            detections.Add(new QrDetection(result.Text, bounds));
        }

        return detections;
    }

    /// <summary>
    /// ZXing QR results return finder centers in this order:
    /// bottom-left, top-left, top-right, and optionally an alignment point.
    /// We reconstruct the full symbol rectangle from finder-center spacing and module size.
    /// A result is rejected when the reconstructed symbol reaches outside the captured frame;
    /// this is what prevents a partially visible QR code from becoming a lock candidate.
    /// </summary>
    private static bool TryCalculateCompleteBounds(
        Result result,
        int frameWidth,
        int frameHeight,
        out PixelRect bounds)
    {
        bounds = default;
        var points = result.ResultPoints;
        if (points is null || points.Length < 3)
            return false;

        var bottomLeft = points[0];
        var topLeft = points[1];
        var topRight = points[2];

        var horizontalDistance = Distance(topLeft, topRight);
        var verticalDistance = Distance(topLeft, bottomLeft);
        if (horizontalDistance < 14 || verticalDistance < 14)
            return false;

        var finderSizes = points
            .Take(3)
            .OfType<FinderPattern>()
            .Select(static point => point.EstimatedModuleSize)
            .Where(static size => size >= 1f)
            .ToArray();

        // QRCodeReader normally returns FinderPattern instances. This fallback keeps the
        // calculation conservative if a future decoder returns plain ResultPoint values.
        var moduleSize = finderSizes.Length > 0
            ? finderSizes.Average()
            : Math.Min(horizontalDistance, verticalDistance) / 14f;

        if (moduleSize < 1f || float.IsNaN(moduleSize) || float.IsInfinity(moduleSize))
            return false;

        var dimension = NormalizeQrDimension(
            (int)Math.Round(((horizontalDistance + verticalDistance) / 2f) / moduleSize) + 7);

        var modulesBetweenFinderCenters = dimension - 7;
        if (modulesBetweenFinderCenters < 14)
            return false;

        var ux = (topRight.X - topLeft.X) / modulesBetweenFinderCenters;
        var uy = (topRight.Y - topLeft.Y) / modulesBetweenFinderCenters;
        var vx = (bottomLeft.X - topLeft.X) / modulesBetweenFinderCenters;
        var vy = (bottomLeft.Y - topLeft.Y) / modulesBetweenFinderCenters;

        // Finder centers are 3.5 modules from each symbol edge.
        const float finderCenterToSymbolEdge = 3.5f;
        var symbolTopLeft = Transform(topLeft, ux, uy, vx, vy, -finderCenterToSymbolEdge, -finderCenterToSymbolEdge);
        var symbolTopRight = Transform(topLeft, ux, uy, vx, vy, dimension - finderCenterToSymbolEdge, -finderCenterToSymbolEdge);
        var symbolBottomLeft = Transform(topLeft, ux, uy, vx, vy, -finderCenterToSymbolEdge, dimension - finderCenterToSymbolEdge);
        var symbolBottomRight = Transform(topLeft, ux, uy, vx, vy, dimension - finderCenterToSymbolEdge, dimension - finderCenterToSymbolEdge);

        var corners = new[] { symbolTopLeft, symbolTopRight, symbolBottomLeft, symbolBottomRight };

        // Require the complete symbol body to stay inside the frame. A small safety inset
        // avoids locking when only an edge fragment is visible.
        const float frameInset = 3f;
        if (corners.Any(point =>
                point.X < frameInset || point.Y < frameInset ||
                point.X > frameWidth - 1 - frameInset ||
                point.Y > frameHeight - 1 - frameInset))
        {
            return false;
        }

        var minX = corners.Min(static point => point.X);
        var minY = corners.Min(static point => point.Y);
        var maxX = corners.Max(static point => point.X);
        var maxY = corners.Max(static point => point.Y);

        // Visual lock box includes a modest margin but remains inside the captured region.
        var visualMargin = Math.Max(4, (int)Math.Ceiling(moduleSize * 1.5f));
        var left = Math.Max(0, (int)Math.Floor(minX) - visualMargin);
        var top = Math.Max(0, (int)Math.Floor(minY) - visualMargin);
        var right = Math.Min(frameWidth, (int)Math.Ceiling(maxX) + visualMargin);
        var bottom = Math.Min(frameHeight, (int)Math.Ceiling(maxY) + visualMargin);

        if (right - left < 20 || bottom - top < 20)
            return false;

        bounds = new PixelRect(left, top, right - left, bottom - top);
        return true;
    }

    private static int NormalizeQrDimension(int dimension)
    {
        // QR dimensions are 21, 25, 29, ... (dimension mod 4 == 1).
        return (dimension & 0x03) switch
        {
            0 => dimension + 1,
            2 => dimension - 1,
            3 => dimension - 2,
            _ => dimension
        };
    }

    private static (float X, float Y) Transform(
        ResultPoint origin,
        float ux,
        float uy,
        float vx,
        float vy,
        float uModules,
        float vModules) =>
        (origin.X + ux * uModules + vx * vModules,
         origin.Y + uy * uModules + vy * vModules);

    private static float Distance(ResultPoint first, ResultPoint second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
