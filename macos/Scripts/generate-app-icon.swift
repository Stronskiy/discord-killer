import AppKit
import Foundation

guard CommandLine.arguments.count == 2 else {
    fputs("Usage: generate-app-icon.swift OUTPUT.icns\n", stderr)
    exit(2)
}

func imagePNG(size: Int) -> Data {
    let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    let context = NSGraphicsContext(bitmapImageRep: bitmap)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = context
    context.imageInterpolation = .high
    let unit = CGFloat(size)
    let rect = CGRect(x: unit * 0.055, y: unit * 0.055, width: unit * 0.89, height: unit * 0.89)
    let tile = NSBezierPath(roundedRect: rect, xRadius: unit * 0.22, yRadius: unit * 0.22)
    NSGradient(colors: [NSColor(calibratedRed: 0.18, green: 0.23, blue: 0.42, alpha: 1),
                        NSColor(calibratedRed: 0.38, green: 0.46, blue: 0.78, alpha: 1)])!
        .draw(in: tile, angle: 100)
    NSColor.white.withAlphaComponent(0.3).setStroke()
    tile.lineWidth = max(1, unit * 0.012)
    tile.stroke()

    let dial = NSBezierPath(ovalIn: CGRect(x: unit * 0.25, y: unit * 0.25, width: unit * 0.5, height: unit * 0.5))
    NSColor.white.setStroke()
    dial.lineWidth = max(1, unit * 0.04)
    dial.stroke()
    let hands = NSBezierPath()
    hands.lineWidth = max(1, unit * 0.04)
    hands.lineCapStyle = .round
    hands.move(to: NSPoint(x: unit * 0.5, y: unit * 0.65))
    hands.line(to: NSPoint(x: unit * 0.5, y: unit * 0.5))
    hands.line(to: NSPoint(x: unit * 0.62, y: unit * 0.43))
    hands.stroke()

    NSColor.white.withAlphaComponent(0.9).setFill()
    NSBezierPath(ovalIn: CGRect(x: unit * 0.69, y: unit * 0.72, width: unit * 0.055, height: unit * 0.055)).fill()
    NSBezierPath(ovalIn: CGRect(x: unit * 0.21, y: unit * 0.2, width: unit * 0.035, height: unit * 0.035)).fill()
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
