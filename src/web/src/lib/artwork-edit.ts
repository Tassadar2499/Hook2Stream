export const coverFontFamilies = [
  { value: "sans", label: "Sans" },
  { value: "serif", label: "Serif" },
  { value: "monospace", label: "Monospace" },
] as const;

export type CoverFontFamily = (typeof coverFontFamilies)[number]["value"];

export type ArtworkEditSpec = {
  cropX: number;
  cropY: number;
  cropScale: number;
  focalX: number;
  focalY: number;
  palette: [string, string, string];
  showArtist: boolean;
  showTitle: boolean;
  fontFamily: CoverFontFamily;
  artistFontSize: number;
  titleFontSize: number;
  textX: number;
  textY: number;
};

const fallbackPalette: ArtworkEditSpec["palette"] = ["#121212", "#fffaf2", "#ff5c35"];

export function createDefaultArtworkEdit(): ArtworkEditSpec {
  return {
    cropX: 0.5,
    cropY: 0.5,
    cropScale: 1,
    focalX: 0.5,
    focalY: 0.5,
    palette: [...fallbackPalette],
    showArtist: true,
    showTitle: true,
    fontFamily: "sans",
    artistFontSize: 112,
    titleFontSize: 188,
    textX: 0,
    textY: 1,
  };
}

export function parseArtworkEdit(value: string): ArtworkEditSpec {
  try {
    return normalizeArtworkEdit(JSON.parse(value));
  } catch {
    return createDefaultArtworkEdit();
  }
}

export function normalizeArtworkEdit(value: unknown): ArtworkEditSpec {
  const source = isRecord(value) ? value : {};
  const palette = Array.isArray(source.palette) ? source.palette : [];

  return {
    cropX: numberWithin(source.cropX, 0.5, 0, 1),
    cropY: numberWithin(source.cropY, 0.5, 0, 1),
    cropScale: numberWithin(source.cropScale, 1, 1, 2),
    // Retained for backward-compatible round trips. Campaign focal-point controls
    // are separate; the clean-cover renderer only exposes controls it honors.
    focalX: numberWithin(source.focalX, 0.5, 0, 1),
    focalY: numberWithin(source.focalY, 0.5, 0, 1),
    palette: [
      colorOrFallback(palette[0], fallbackPalette[0]),
      colorOrFallback(palette[1], fallbackPalette[1]),
      colorOrFallback(palette[2], fallbackPalette[2]),
    ],
    showArtist: booleanOrFallback(source.showArtist, true),
    showTitle: booleanOrFallback(source.showTitle, true),
    fontFamily: fontFamilyOrFallback(source.fontFamily),
    artistFontSize: integerWithin(source.artistFontSize, 112, 72, 220),
    titleFontSize: integerWithin(source.titleFontSize, 188, 96, 360),
    textX: numberWithin(source.textX, 0, 0, 1),
    textY: numberWithin(source.textY, 1, 0, 1),
  };
}

export function coverFontCss(fontFamily: CoverFontFamily) {
  switch (fontFamily) {
    case "serif":
      return "'Noto Serif', Georgia, 'Times New Roman', serif";
    case "monospace":
      return "'Noto Sans Mono', 'Courier New', Courier, monospace";
    default:
      return "'Noto Sans', Arial, Helvetica, sans-serif";
  }
}

export function coverHexWithAlpha(color: string, opacity: number) {
  const normalized = colorOrFallback(color, fallbackPalette[0]);
  const alpha = Math.min(1, Math.max(0, Number.isFinite(opacity) ? opacity : 1));
  return `rgba(${Number.parseInt(normalized.slice(1, 3), 16)}, ${Number.parseInt(normalized.slice(3, 5), 16)}, ${Number.parseInt(normalized.slice(5, 7), 16)}, ${alpha})`;
}

export function coverImageCropStyle(
  edit: Pick<ArtworkEditSpec, "cropX" | "cropY" | "cropScale">,
) {
  const cropX = numberWithin(edit.cropX, 0.5, 0, 1);
  const cropY = numberWithin(edit.cropY, 0.5, 0, 1);
  const cropScale = numberWithin(edit.cropScale, 1, 1, 2);

  return {
    width: `${cropScale * 100}%`,
    height: `${cropScale * 100}%`,
    left: `${-(cropScale - 1) * cropX * 100}%`,
    top: `${-(cropScale - 1) * cropY * 100}%`,
  } as const;
}

export function coverTextLayoutStyle(
  edit: Pick<
    ArtworkEditSpec,
    | "artistFontSize"
    | "titleFontSize"
    | "textX"
    | "textY"
    | "showArtist"
    | "showTitle"
  >,
) {
  const artistFontSize = integerWithin(edit.artistFontSize, 112, 72, 220);
  const titleFontSize = integerWithin(edit.titleFontSize, 188, 96, 360);
  const textX = numberWithin(edit.textX, 0, 0, 1);
  const textY = numberWithin(edit.textY, 1, 0, 1);
  const blockHeight =
    (edit.showArtist ? artistFontSize : 0) +
    (edit.showArtist && edit.showTitle ? 48 : 0) +
    (edit.showTitle ? titleFontSize : 0);
  const originY = 180 + Math.round(Math.max(0, 3000 - 360 - blockHeight) * textY);
  const boxY = Math.max(0, originY - 60);
  const boxHeight = Math.min(3000 - boxY, blockHeight + 120);
  const titleY = originY + (edit.showArtist ? artistFontSize + (edit.showTitle ? 48 : 0) : 0);

  return {
    bandTop: percentOfCover(boxY),
    bandHeight: percentOfCover(boxHeight),
    artistTop: percentOfCover(originY),
    titleTop: percentOfCover(titleY),
    textLeft: formatPercent(6 + textX * 88),
    textTransform: `translateX(${-textX * 100}%)`,
  } as const;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function numberWithin(
  value: unknown,
  fallback: number,
  minimum: number,
  maximum: number,
) {
  return typeof value === "number" && Number.isFinite(value)
    ? Math.min(maximum, Math.max(minimum, value))
    : fallback;
}

function integerWithin(
  value: unknown,
  fallback: number,
  minimum: number,
  maximum: number,
) {
  return typeof value === "number" && Number.isFinite(value)
    ? Math.round(Math.min(maximum, Math.max(minimum, value)))
    : fallback;
}

function booleanOrFallback(value: unknown, fallback: boolean) {
  return typeof value === "boolean" ? value : fallback;
}

function fontFamilyOrFallback(value: unknown): CoverFontFamily {
  const normalized = typeof value === "string" ? value.toLowerCase() : undefined;
  return coverFontFamilies.some((candidate) => candidate.value === normalized)
    ? (normalized as CoverFontFamily)
    : "sans";
}

function colorOrFallback(value: unknown, fallback: string) {
  return typeof value === "string" && /^#[0-9a-f]{6}$/i.test(value)
    ? value.toLowerCase()
    : fallback;
}

function percentOfCover(pixels: number) {
  return formatPercent(pixels / 30);
}

function formatPercent(value: number) {
  return `${Number(value.toFixed(4))}%`;
}
