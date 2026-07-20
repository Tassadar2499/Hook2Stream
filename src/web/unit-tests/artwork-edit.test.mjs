import assert from "node:assert/strict";
import test from "node:test";
import {
  coverFontCss,
  coverHexWithAlpha,
  coverImageCropStyle,
  coverTextLayoutStyle,
  normalizeArtworkEdit,
  parseArtworkEdit,
} from "../src/lib/artwork-edit.ts";

test("legacy cover compositions receive supported typography defaults", () => {
  const edit = parseArtworkEdit(JSON.stringify({
    cropX: 0.25,
    palette: ["#010203", "#aabbcc", "#DDEEFF"],
    showArtist: false,
  }));

  assert.equal(edit.cropX, 0.25);
  assert.deepEqual(edit.palette, ["#010203", "#aabbcc", "#ddeeff"]);
  assert.equal(edit.showArtist, false);
  assert.equal(edit.fontFamily, "sans");
  assert.equal(edit.artistFontSize, 112);
  assert.equal(edit.titleFontSize, 188);
  assert.equal(edit.textX, 0);
  assert.equal(edit.textY, 1);
});

test("normalization allowlists fonts, clamps controls and preserves palette slots", () => {
  const edit = normalizeArtworkEdit({
    cropX: -4,
    cropY: Number.NaN,
    cropScale: 7,
    palette: ["red", "#ABCDEF", "#123456"],
    fontFamily: "Sans'; text='unsafe",
    artistFontSize: 109.5,
    titleFontSize: 900,
    textX: 4,
    textY: -2,
    showTitle: "yes",
  });

  assert.equal(edit.cropX, 0);
  assert.equal(edit.cropY, 0.5);
  assert.equal(edit.cropScale, 2);
  assert.deepEqual(edit.palette, ["#121212", "#abcdef", "#123456"]);
  assert.equal(edit.fontFamily, "sans");
  assert.equal(edit.artistFontSize, 110);
  assert.equal(edit.titleFontSize, 360);
  assert.equal(edit.textX, 1);
  assert.equal(edit.textY, 0);
  assert.equal(edit.showTitle, true);
  assert.equal(normalizeArtworkEdit({ fontFamily: "SERIF" }).fontFamily, "serif");
});

test("preview helpers reflect the chosen palette, font and safe anchor", () => {
  assert.equal(coverHexWithAlpha("#336699", 0.58), "rgba(51, 102, 153, 0.58)");
  assert.equal(coverFontCss("serif"), "'Noto Serif', Georgia, 'Times New Roman', serif");
  assert.deepEqual(coverImageCropStyle({ cropX: 0.25, cropY: 0.75, cropScale: 1.5 }), {
    width: "150%",
    height: "150%",
    left: "-12.5%",
    top: "-37.5%",
  });
  assert.deepEqual(coverTextLayoutStyle({
    artistFontSize: 100,
    titleFontSize: 200,
    textX: 0.75,
    textY: 0.25,
    showArtist: true,
    showTitle: true,
  }), {
    bandTop: "23.1%",
    bandHeight: "15.6%",
    artistTop: "25.1%",
    titleTop: "30.0333%",
    textLeft: "72%",
    textTransform: "translateX(-75%)",
  });
});
