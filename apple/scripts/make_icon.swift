import Foundation
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

let size = 1024
let args = CommandLine.arguments
guard args.count == 2 else {
    FileHandle.standardError.write(Data("usage: make_icon <output.png>\n".utf8))
    exit(2)
}

let space = CGColorSpace(name: CGColorSpace.displayP3) ?? CGColorSpaceCreateDeviceRGB()
guard let ctx = CGContext(
    data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
    space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
) else { exit(1) }

func color(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat = 1) -> CGColor {
    CGColor(colorSpace: space, components: [r, g, b, a]) ?? CGColor(red: r, green: g, blue: b, alpha: a)
}

func roundedRect(_ rect: CGRect, _ radius: CGFloat) -> CGPath {
    CGPath(roundedRect: rect, cornerWidth: radius, cornerHeight: radius, transform: nil)
}

let tile = CGRect(x: 100, y: 100, width: 824, height: 824)
let tilePath = roundedRect(tile, 186)

ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -14), blur: 28, color: color(0, 0, 0, 0.28))
ctx.addPath(tilePath)
ctx.setFillColor(color(0.36, 0.40, 0.98))
ctx.fillPath()
ctx.restoreGState()

ctx.saveGState()
ctx.addPath(tilePath)
ctx.clip()
let bg = CGGradient(colorsSpace: space, colors: [color(0.33, 0.47, 1.0), color(0.46, 0.33, 0.98), color(0.62, 0.26, 0.90)] as CFArray, locations: [0, 0.55, 1])!
ctx.drawLinearGradient(bg, start: CGPoint(x: 150, y: 924), end: CGPoint(x: 874, y: 100), options: [])
let sheen = CGGradient(colorsSpace: space, colors: [color(1, 1, 1, 0.22), color(1, 1, 1, 0)] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(sheen, start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 560), options: [])
ctx.restoreGState()

ctx.saveGState()
ctx.addPath(tilePath)
ctx.setStrokeColor(color(1, 1, 1, 0.18))
ctx.setLineWidth(3)
ctx.strokePath()
ctx.restoreGState()

let board = CGRect(x: 302, y: 214, width: 420, height: 540)
ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 24, color: color(0.12, 0.05, 0.35, 0.35))
ctx.addPath(roundedRect(board, 60))
ctx.setFillColor(color(1, 1, 1, 0.97))
ctx.fillPath()
ctx.restoreGState()

let lineColor = color(0.42, 0.38, 0.95, 0.28)
let lines: [(CGFloat, CGFloat)] = [(600, 300), (530, 260), (460, 300), (390, 190)]
for (y, w) in lines {
    ctx.addPath(roundedRect(CGRect(x: 362, y: y, width: w, height: 30), 15))
    ctx.setFillColor(lineColor)
    ctx.fillPath()
}

let clip = CGRect(x: 402, y: 700, width: 220, height: 104)
ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -6), blur: 12, color: color(0.1, 0.05, 0.3, 0.3))
ctx.addPath(roundedRect(clip, 34))
let clipGradient = CGGradient(colorsSpace: space, colors: [color(0.98, 0.98, 1.0), color(0.86, 0.87, 0.97)] as CFArray, locations: [0, 1])!
ctx.clip()
ctx.drawLinearGradient(clipGradient, start: CGPoint(x: 512, y: 804), end: CGPoint(x: 512, y: 700), options: [])
ctx.restoreGState()
ctx.addPath(roundedRect(CGRect(x: 472, y: 748, width: 80, height: 26), 13))
ctx.setFillColor(color(0.46, 0.36, 0.96, 0.85))
ctx.fillPath()

guard let image = ctx.makeImage() else { exit(1) }
let url = URL(fileURLWithPath: args[1])
guard let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil) else { exit(1) }
CGImageDestinationAddImage(dest, image, nil)
exit(CGImageDestinationFinalize(dest) ? 0 : 1)
