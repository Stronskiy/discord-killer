import AppKit
import Foundation

// Original, self-contained Finder window artwork.
let size = NSSize(width: 640, height: 400)
let image = NSImage(size: size)
image.lockFocus()
let bounds = NSRect(origin: .zero, size: size)
let gradient = NSGradient(starting: NSColor(calibratedRed: 0.95, green: 0.96, blue: 1.0, alpha: 1),
                          ending: NSColor(calibratedRed: 0.87, green: 0.89, blue: 0.98, alpha: 1))!
gradient.draw(in: bounds, angle: 270)
let paragraph = NSMutableParagraphStyle()
paragraph.alignment = .center
let attributes: [NSAttributedString.Key: Any] = [
    .font: NSFont.systemFont(ofSize: 19, weight: .semibold),
    .foregroundColor: NSColor(calibratedRed: 0.20, green: 0.23, blue: 0.39, alpha: 1),
    .paragraphStyle: paragraph
]
"Drag Discord Timer to Applications".draw(in: NSRect(x: 40, y: 320, width: 560, height: 32), withAttributes: attributes)
let arrow = NSBezierPath()
arrow.lineWidth = 4
arrow.lineCapStyle = .round
arrow.lineJoinStyle = .round
arrow.move(to: NSPoint(x: 275, y: 190))
arrow.line(to: NSPoint(x: 365, y: 190))
arrow.move(to: NSPoint(x: 351, y: 204))
arrow.line(to: NSPoint(x: 365, y: 190))
arrow.line(to: NSPoint(x: 351, y: 176))
NSColor(calibratedRed: 0.35, green: 0.37, blue: 0.68, alpha: 0.8).setStroke()
arrow.stroke()
image.unlockFocus()
let rep = NSBitmapImageRep(data: image.tiffRepresentation!)!
let png = rep.representation(using: .png, properties: [:])!
try png.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
