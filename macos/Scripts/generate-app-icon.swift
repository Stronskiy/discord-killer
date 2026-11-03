import AppKit
import Foundation

guard CommandLine.arguments.count == 2 else {
    fputs("Usage: generate-app-icon.swift OUTPUT.icns\n", stderr)
    exit(2)
}

// Original artwork: a soft glass tile, a generic game controller, an interruption
// slash and a separate clock badge. Coordinates are normalized for all icon sizes.
func imagePNG(size: Int) -> Data {
    let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    let context = NSGraphicsContext(bitmapImageRep: bitmap)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = context
    context.imageInterpolation = .high
    context.cgContext.setAllowsAntialiasing(true)
    let u = CGFloat(size)
    func p(_ x: CGFloat, _ y: CGFloat) -> NSPoint { NSPoint(x: x * u, y: y * u) }
    func r(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat) -> NSRect {
        NSRect(x: x * u, y: y * u, width: w * u, height: h * u)
    }
    let tile = NSBezierPath(roundedRect: r(0.055, 0.055, 0.89, 0.89), xRadius: u * 0.22, yRadius: u * 0.22)
    NSGradient(colors: [NSColor(calibratedRed: 0.31, green: 0.25, blue: 0.72, alpha: 1),
                        NSColor(calibratedRed: 0.57, green: 0.49, blue: 0.94, alpha: 1)])!
        .draw(in: tile, angle: 90)
    NSGraphicsContext.saveGraphicsState()
    tile.addClip()
    let light = NSBezierPath(ovalIn: r(-0.17, 0.54, 1.10, 0.53))
    NSGradient(colors: [NSColor.white.withAlphaComponent(0.25), .clear])!.draw(in: light, angle: 260)
    let glow = NSBezierPath(ovalIn: r(0.23, -0.29, 1.07, 0.69))
    NSGradient(colors: [NSColor(calibratedRed: 0.65, green: 0.56, blue: 1, alpha: 0.30), .clear])!
        .draw(in: glow, angle: 90)
    NSGraphicsContext.restoreGraphicsState()
    NSColor.white.withAlphaComponent(0.52).setStroke()
    tile.lineWidth = max(0.6, u * 0.008)
    tile.stroke()

    // Original, geometric controller silhouette, not Discord's mark.
    let pad = NSBezierPath()
    pad.move(to: p(0.27, 0.60))
    pad.curve(to: p(0.20, 0.49), controlPoint1: p(0.23, 0.59), controlPoint2: p(0.21, 0.54))
    pad.line(to: p(0.16, 0.36))
    pad.curve(to: p(0.24, 0.28), controlPoint1: p(0.14, 0.28), controlPoint2: p(0.20, 0.25))
    pad.line(to: p(0.35, 0.36))
    pad.line(to: p(0.62, 0.36))
    pad.line(to: p(0.72, 0.28))
    pad.curve(to: p(0.81, 0.36), controlPoint1: p(0.77, 0.25), controlPoint2: p(0.83, 0.28))
    pad.line(to: p(0.77, 0.49))
    pad.curve(to: p(0.70, 0.60), controlPoint1: p(0.76, 0.54), controlPoint2: p(0.74, 0.59))
    pad.close()
    NSGradient(colors: [NSColor(calibratedRed: 0.77, green: 0.78, blue: 1, alpha: 0.94),
                        NSColor.white.withAlphaComponent(0.96)])!.draw(in: pad, angle: 90)
    NSColor.white.withAlphaComponent(0.70).setStroke()
    pad.lineWidth = max(0.5, u * 0.006)
    pad.stroke()

    let detail = NSColor(calibratedRed: 0.31, green: 0.28, blue: 0.68, alpha: 0.95)
    detail.setStroke()
    let cross = NSBezierPath()
    cross.lineWidth = max(1, u * 0.025)
    cross.lineCapStyle = .round
    cross.move(to: p(0.30, 0.47)); cross.line(to: p(0.40, 0.47))
    cross.move(to: p(0.35, 0.42)); cross.line(to: p(0.35, 0.52))
    cross.stroke()
    detail.setFill()
    for (x, y): (CGFloat, CGFloat) in [(0.63, 0.50), (0.68, 0.46), (0.58, 0.46), (0.63, 0.42)] {
        NSBezierPath(ovalIn: r(x - 0.013, y - 0.013, 0.026, 0.026)).fill()
    }

    // Clear, high-contrast interruption stroke from top-left to bottom-right.
    let slash = NSBezierPath()
    slash.lineCapStyle = .round
    slash.move(to: p(0.24, 0.73)); slash.line(to: p(0.72, 0.24))
    NSColor(calibratedRed: 0.22, green: 0.16, blue: 0.54, alpha: 0.38).setStroke()
    slash.lineWidth = max(2, u * 0.065); slash.stroke()
    NSColor(calibratedRed: 0.98, green: 0.97, blue: 1, alpha: 0.98).setStroke()
    slash.lineWidth = max(1, u * 0.036); slash.stroke()

    // Timer is drawn after the slash, at the top right, for small-size clarity.
    let badge = NSBezierPath(ovalIn: r(0.62, 0.61, 0.24, 0.24))
    NSGradient(colors: [NSColor(calibratedRed: 0.35, green: 0.31, blue: 0.75, alpha: 0.98),
                        NSColor(calibratedRed: 0.56, green: 0.52, blue: 0.94, alpha: 0.98)])!
        .draw(in: badge, angle: 90)
    NSColor.white.withAlphaComponent(0.95).setStroke()
    badge.lineWidth = max(0.7, u * 0.012); badge.stroke()
    let clock = NSBezierPath()
    clock.lineWidth = max(0.7, u * 0.014); clock.lineCapStyle = .round
    clock.move(to: p(0.74, 0.79)); clock.line(to: p(0.74, 0.73)); clock.line(to: p(0.78, 0.76))
    clock.stroke()
    let crown = NSBezierPath()
    crown.lineWidth = max(0.7, u * 0.016); crown.lineCapStyle = .round
    crown.move(to: p(0.71, 0.86)); crown.line(to: p(0.77, 0.86)); crown.stroke()

    context.flushGraphics()
    NSGraphicsContext.restoreGraphicsState()
    guard let png = bitmap.representation(using: .png, properties: [:]) else { fatalError("PNG encoding failed") }
    return png
}

var chunks = Data()
for (kind, size) in [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128),
                     ("ic08", 256), ("ic09", 512), ("ic10", 1024)] {
    let png = imagePNG(size: size)
    chunks.append(contentsOf: kind.utf8)
    var length = UInt32(png.count + 8).bigEndian
    withUnsafeBytes(of: &length) { chunks.append(contentsOf: $0) }
    chunks.append(png)
}
var icon = Data("icns".utf8)
var length = UInt32(chunks.count + 8).bigEndian
withUnsafeBytes(of: &length) { icon.append(contentsOf: $0) }
icon.append(chunks)
try icon.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
