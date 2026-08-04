#!/usr/bin/env python3
"""Build the single-atlas SuAPI font resources from the existing source assets."""

from __future__ import annotations

import gzip
import io
import struct
from dataclasses import dataclass
from pathlib import Path

from PIL import Image


ATLAS_WIDTH = 4096
SOURCE_ATLAS_HEIGHT = 4096
CROP_ALIGNMENT = 16
PERICLES_SCALE = 0.632
RAW_CHINESE_HEIGHT = 43.0
PROFILE_SOURCES = (
    (0, "Pericles12", 24.0),
    (1, "Pericles12u", 24.0),
    (2, "Pericles18", 34.0),
    (3, "Pericles18u", 34.0),
    (4, "Pericles24", 45.0),
    (5, "Pericles32", 59.0),
)


@dataclass(frozen=True)
class Glyph:
    code: int
    left: float
    top: float
    right: float
    bottom: float
    offset_x: float
    offset_y: float
    width: float


@dataclass(frozen=True)
class Kerning:
    first: int
    second: int
    amount: float


class AtlasPacker:
    def __init__(self, image: Image.Image, start_y: int) -> None:
        self.image = image
        self.x = 2
        self.y = start_y + 2
        self.row_height = 0

    def add(self, sprite: Image.Image) -> tuple[int, int]:
        width, height = sprite.size
        if self.x + width + 2 > self.image.width:
            self.x = 2
            self.y += self.row_height + 2
            self.row_height = 0
        if self.y + height + 2 > self.image.height:
            raise RuntimeError("The shared font atlas has no room for Pericles glyphs.")
        position = (self.x, self.y)
        self.image.alpha_composite(sprite, position)
        self.x += width + 2
        self.row_height = max(self.row_height, height)
        return position

    @property
    def used_height(self) -> int:
        return self.y + self.row_height + 2


class GameRandom:
    """Minimal PK2 pad generator.

    Source: Survivalcraft/Game/Random.cs:Random.UInt
    """

    def __init__(self, seed: int) -> None:
        self.s0 = self._hash(seed)
        self.s1 = self._hash(seed + 1)

    @staticmethod
    def _hash(key: int) -> int:
        key &= 0xFFFFFFFF
        key ^= key >> 16
        key = (key * 2146121005) & 0xFFFFFFFF
        key ^= key >> 15
        key = (key * 2221713035) & 0xFFFFFFFF
        key ^= key >> 16
        return key & 0xFFFFFFFF

    @staticmethod
    def _rotate_left(value: int, amount: int) -> int:
        return ((value << amount) | (value >> (32 - amount))) & 0xFFFFFFFF

    def integer(self, bound: int) -> int:
        source = self.s0
        mixed = self.s1 ^ source
        self.s0 = self._rotate_left(source, 26) ^ mixed ^ ((mixed << 9) & 0xFFFFFFFF)
        self.s0 &= 0xFFFFFFFF
        self.s1 = self._rotate_left(mixed, 13)
        value = self._rotate_left((source * 2654435771) & 0xFFFFFFFF, 5)
        value = (value * 5) & 0xFFFFFFFF
        return ((value & 0x7FFFFFFF) * bound) // 2147483648


def read_7bit_int(data: bytes, offset: int) -> tuple[int, int]:
    value = 0
    shift = 0
    while True:
        current = data[offset]
        offset += 1
        value |= (current & 0x7F) << shift
        if (current & 0x80) == 0:
            return value, offset
        shift += 7


def read_pericles_kernings(pak_path: Path) -> dict[str, list[Kerning]]:
    """Reads native Pericles pair spacing from the source content package.

    Source: Survivalcraft/Content.pak:Fonts/Pericles* (Engine.Media.BitmapFont)
    Source: Engine/Engine/Content/BitmapFontContentReader.cs:InitializeBitmapFont
    """
    raw = pak_path.read_bytes()
    if raw[:4] != b"PK2\0":
        raise RuntimeError(f"Unexpected content package format: {pak_path}")

    alphabet = "0123456789abdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"
    random = GameRandom(9217)
    toc_pad = bytes(ord(alphabet[random.integer(len(alphabet))]) for _ in range(229))

    def xor_bytes(data: bytes, absolute_offset: int) -> bytes:
        return bytes(value ^ toc_pad[(absolute_offset + index) % len(toc_pad)]
                     for index, value in enumerate(data))

    def read_xor_int64(offset: int) -> int:
        return struct.unpack("<q", xor_bytes(raw[offset:offset + 8], offset))[0]

    def read_xor_int32(offset: int) -> int:
        return struct.unpack("<i", xor_bytes(raw[offset:offset + 4], offset))[0]

    def read_xor_string(offset: int) -> tuple[str, int]:
        length = 0
        shift = 0
        while True:
            current = raw[offset] ^ toc_pad[offset % len(toc_pad)]
            offset += 1
            length |= (current & 0x7F) << shift
            if (current & 0x80) == 0:
                break
            shift += 7
        return xor_bytes(raw[offset:offset + length], offset).decode("utf-8"), offset + length

    offset = 4
    content_offset = read_xor_int64(offset)
    offset += 8
    entry_count = read_xor_int32(offset)
    offset += 4
    requested = {f"Fonts/{name}" for _, name, _ in PROFILE_SOURCES}
    entries: dict[str, tuple[int, int]] = {}
    for _ in range(entry_count):
        name, offset = read_xor_string(offset)
        _, offset = read_xor_string(offset)
        position = read_xor_int64(offset)
        offset += 8
        size = read_xor_int64(offset)
        offset += 8
        if name in requested:
            entries[name] = (content_offset + position, size)

    if len(entries) != len(requested):
        missing = ", ".join(sorted(requested - entries.keys()))
        raise RuntimeError(f"Missing native Pericles font entries: {missing}")

    result: dict[str, list[Kerning]] = {}
    for _, name, _ in PROFILE_SOURCES:
        position, size = entries[f"Fonts/{name}"]
        font_data = bytes(value ^ 0x3F for value in raw[position:position + size])
        glyph_count = struct.unpack_from("<i", font_data, 0)[0]
        # Every native Pericles glyph code is printable ASCII and serialized as one UTF-8 byte.
        cursor = 4 + glyph_count * 29
        cursor += 4 + 8 + 4 + 1  # GlyphHeight, Spacing, Scale, fallback code.
        kerning_count = struct.unpack_from("<i", font_data, cursor)[0]
        cursor += 4
        kernings: list[Kerning] = []
        for _ in range(kerning_count):
            first, cursor = read_7bit_int(font_data, cursor)
            second, cursor = read_7bit_int(font_data, cursor)
            amount, cursor = read_7bit_int(font_data, cursor)
            if 32 <= first <= 125 and 32 <= second <= 125:
                kernings.append(Kerning(first, second, float(amount)))
        result[name] = kernings
    return result


def parse_chinese_data(path: Path) -> tuple[float, int, dict[int, Glyph], list[Kerning]]:
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    glyph_count = int(lines[0])
    glyphs: dict[int, Glyph] = {}
    for line in lines[1 : 1 + glyph_count]:
        parts = line.split()
        if len(parts) == 7:
            parts.insert(0, " ")
        if len(parts) < 8:
            continue
        glyph = Glyph(
            ord(parts[0][0]),
            float(parts[1]),
            float(parts[2]),
            float(parts[3]),
            float(parts[4]),
            float(parts[5]),
            float(parts[6]),
            float(parts[7]),
        )
        glyphs[glyph.code] = glyph

    metrics_index = 1 + glyph_count
    glyph_height = float(lines[metrics_index])
    fallback = ord(lines[metrics_index + 3][0])
    kerning_count = int(lines[metrics_index + 4])
    kernings: list[Kerning] = []
    for line in lines[metrics_index + 5 : metrics_index + 5 + kerning_count]:
        parts = line.split()
        if len(parts) >= 3:
            kernings.append(Kerning(ord(parts[0][0]), ord(parts[1][0]), float(parts[2])))
    return glyph_height, fallback, glyphs, kernings


def parse_pericles_data(path: Path) -> tuple[list[Glyph], float]:
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    glyph_count = int(lines[0])
    glyphs: list[Glyph] = []
    for code, line in enumerate(lines[1 : 1 + glyph_count]):
        parts = line.split()
        if len(parts) == 7:
            parts.insert(0, " ")
        glyphs.append(
            Glyph(
                code,
                float(parts[1]),
                float(parts[2]),
                float(parts[3]),
                float(parts[4]),
                float(parts[5]),
                float(parts[6]),
                float(parts[7]),
            )
        )
    return glyphs, float(lines[1 + glyph_count + 2])


def crop_glyph(image: Image.Image, glyph: Glyph) -> Image.Image:
    left = round(glyph.left * image.width)
    top = round(glyph.top * image.height)
    right = round(glyph.right * image.width)
    bottom = round(glyph.bottom * image.height)
    if right <= left or bottom <= top:
        return Image.new("RGBA", (0, 0))
    return image.crop((left, top, right, bottom))


def write_glyphs(writer: io.BufferedWriter, glyphs: list[Glyph]) -> None:
    writer.write(struct.pack("<i", len(glyphs)))
    for glyph in glyphs:
        writer.write(
            struct.pack(
                "<H7f",
                glyph.code,
                glyph.left,
                glyph.top,
                glyph.right,
                glyph.bottom,
                glyph.offset_x,
                glyph.offset_y,
                glyph.width,
            )
        )


def write_kernings(writer: io.BufferedWriter, kernings: list[Kerning]) -> None:
    writer.write(struct.pack("<i", len(kernings)))
    for kerning in kernings:
        writer.write(struct.pack("<HHf", kerning.first, kerning.second, kerning.amount))


def find_empty_start_y(image: Image.Image) -> int:
    alpha = image.getchannel("A")
    for y in range(image.height):
        if alpha.crop((0, y, image.width, y + 1)).getbbox() is None:
            return y
    raise RuntimeError("The Chinese atlas has no empty rows for profile glyphs.")


def remap_vertical_coordinates(glyph: Glyph, source_height: int,
                               output_height: int) -> Glyph:
    """Retarget normalized V coordinates after removing unused atlas rows."""
    return Glyph(
        glyph.code,
        glyph.left,
        glyph.top * source_height / output_height,
        glyph.right,
        glyph.bottom * source_height / output_height,
        glyph.offset_x,
        glyph.offset_y,
        glyph.width,
    )


def build() -> None:
    # This generator is kept with the Mod repository's source assets.  The
    # generated files are written into the main repository's runtime resource
    # directory and are the only font files consumed by SuAPICore.csproj.
    root = Path(__file__).resolve().parents[2]
    chinese_root = Path(__file__).resolve().parent
    pericles_root = root / "Pak" / "Fonts"
    output_root = root / "EntitySystem" / "SuAPICore" / "Resources" / "Fonts"
    output_root.mkdir(parents=True, exist_ok=True)
    pericles_kernings = read_pericles_kernings(root / "Survivalcraft" / "Content.pak")

    glyph_height, fallback, base_glyphs, base_kernings = parse_chinese_data(
        chinese_root / "chinese32data.txt"
    )
    if glyph_height != RAW_CHINESE_HEIGHT:
        raise RuntimeError(f"Expected Chinese glyph height {RAW_CHINESE_HEIGHT}, got {glyph_height}.")

    atlas = Image.open(chinese_root / "chinese32.png").convert("RGBA")
    if atlas.size != (ATLAS_WIDTH, SOURCE_ATLAS_HEIGHT):
        raise RuntimeError(
            f"Expected {ATLAS_WIDTH}x{SOURCE_ATLAS_HEIGHT} Chinese atlas, got {atlas.size}."
        )
    packer = AtlasPacker(atlas, find_empty_start_y(atlas))
    profile_glyphs: list[tuple[int, float, list[Glyph], list[Kerning]]] = []

    for profile_id, profile_name, pericles_height in PROFILE_SOURCES:
        source_glyphs, source_scale = parse_pericles_data(
            pericles_root / f"{profile_name}.lst"
        )
        if source_scale != PERICLES_SCALE:
            raise RuntimeError(f"Unexpected Pericles scale in {profile_name}.")
        source_image = Image.open(pericles_root / f"!{profile_name}.png").convert("RGBA")
        # All profiles use a scale derived from the native Pericles line height.
        # This makes the shared 43px Chinese atlas reach the same displayed height
        # as each Pericles profile, including Pericles32 (59px).
        target_scale = PERICLES_SCALE * pericles_height / RAW_CHINESE_HEIGHT
        raster_scale = PERICLES_SCALE / target_scale
        overrides: list[Glyph] = []

        for source_glyph in source_glyphs:
            if source_glyph.code < 32 or source_glyph.code > 125:
                continue
            if source_glyph.code == 32:
                overrides.append(
                    Glyph(
                        source_glyph.code,
                        0.0,
                        0.0,
                        0.0,
                        0.0,
                        source_glyph.offset_x * raster_scale,
                        source_glyph.offset_y * raster_scale,
                        source_glyph.width * raster_scale,
                    )
                )
                continue

            sprite = crop_glyph(source_image, source_glyph)
            scaled_size = (
                max(1, round(sprite.width * raster_scale)),
                max(1, round(sprite.height * raster_scale)),
            )
            # Source: Pak/Fonts/!Pericles12.png through !Pericles32.png
            # Bicubic avoids the light/dark ringing that Lanczos creates once the
            # game applies its own linear texture filter to the scaled font atlas.
            if sprite.size != scaled_size:
                sprite = sprite.resize(scaled_size, Image.Resampling.BICUBIC)
            x, y = packer.add(sprite)
            overrides.append(
                Glyph(
                    source_glyph.code,
                    x / ATLAS_WIDTH,
                    y / SOURCE_ATLAS_HEIGHT,
                    (x + sprite.width) / ATLAS_WIDTH,
                    (y + sprite.height) / SOURCE_ATLAS_HEIGHT,
                    source_glyph.offset_x * raster_scale,
                    source_glyph.offset_y * raster_scale,
                    source_glyph.width * raster_scale,
                )
            )
        # BitmapFont stores kerning in integer atlas pixels. Keep it in the same
        # coordinate system as the resized glyph widths before the profile Scale
        # is applied at draw time.
        scaled_kernings = [
            Kerning(record.first, record.second,
                    float(round(record.amount * raster_scale)))
            for record in pericles_kernings[profile_name]
        ]
        profile_glyphs.append((profile_id, target_scale, overrides, scaled_kernings))

    output_height = (
        (packer.used_height + CROP_ALIGNMENT - 1) // CROP_ALIGNMENT
    ) * CROP_ALIGNMENT
    if output_height > SOURCE_ATLAS_HEIGHT:
        raise RuntimeError("The shared font atlas crop height exceeds its source height.")
    atlas = atlas.crop((0, 0, ATLAS_WIDTH, output_height))
    base_glyph_list = [
        remap_vertical_coordinates(base_glyphs[code], SOURCE_ATLAS_HEIGHT, output_height)
        for code in sorted(base_glyphs)
    ]
    profile_glyphs = [
        (
            profile_id,
            scale,
            [
                remap_vertical_coordinates(glyph, SOURCE_ATLAS_HEIGHT, output_height)
                for glyph in overrides
            ],
            kernings,
        )
        for profile_id, scale, overrides, kernings in profile_glyphs
    ]

    for red, green, blue, _ in atlas.get_flattened_data():
        if red != green or green != blue:
            raise RuntimeError("The shared font atlas is expected to be grayscale.")
    atlas_path = output_root / "SuAPIChinese32.png"
    atlas.convert("LA").save(atlas_path, optimize=True, compress_level=9)

    data_path = output_root / "SuAPIChinese32.bin"
    with data_path.open("wb") as raw_stream:
        with gzip.GzipFile(fileobj=raw_stream, mode="wb", compresslevel=9, mtime=0) as writer:
            writer.write(b"SUAF")
            writer.write(struct.pack("<BfH", 2, glyph_height, fallback))
            write_glyphs(writer, base_glyph_list)
            write_kernings(writer, base_kernings)
            writer.write(struct.pack("<B", len(profile_glyphs)))
            for profile_id, scale, overrides, kernings in profile_glyphs:
                writer.write(struct.pack("<Bf", profile_id, scale))
                write_glyphs(writer, overrides)
                write_kernings(writer, kernings)

    print(f"Atlas: {atlas_path} {atlas.size} ({atlas_path.stat().st_size} bytes)")
    print(f"Data:  {data_path} ({data_path.stat().st_size} bytes)")
    print(f"Profile glyphs: {sum(len(glyphs) for _, _, glyphs, _ in profile_glyphs)}")
    print(f"Profile kernings: {sum(len(kernings) for _, _, _, kernings in profile_glyphs)}")


if __name__ == "__main__":
    build()
