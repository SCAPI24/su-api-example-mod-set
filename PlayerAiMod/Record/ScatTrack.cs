using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 录制下来的一帧：**原始输入层**（人这一帧按了什么）＋ 便于人读的意图快照。
    ///
    /// 为什么录"原始输入"而不是录"意图"：
    ///   `ComponentInput.PlayerInput`（Move/Look/Jump/Dig/Hit/…）是游戏**从原始输入推导**出来的
    ///   （ComponentInput.cs:160-250：灵敏度换算、wasd 合成、鼠标键→Dig/Hit）。
    ///   回放时如果直接写 PlayerInput，会在同一帧里被游戏自己重新推导覆盖掉 —— 写不进去。
    ///   所以回放走**同一条原始输入通道**（键盘数组/鼠标/滚轮/视角增量），让游戏推导出同样的意图，
    ///   这样"录什么就重放什么"，而且复用 CM-1/CM-2 已经验证过的注入路径。
    ///
    /// 意图字段仍然记下来：一是可读（调试/编辑器显示），二是跨输入设备（手柄/触屏）时留证据。
    /// </summary>
    public struct RecordingFrame
    {
        /// <summary>与上一帧的间隔（毫秒，0~65535）。</summary>
        public int DeltaMs;

        /// <summary>这一帧按下的键（键名，映射见 <see cref="ScatTrack.KeyNames"/>）。</summary>
        public byte[] KeysHeld;

        /// <summary>这一帧刚按下的键（引擎的"downOnce"语义：开背包、切潜行这类开关靠它）。</summary>
        public byte[] KeysPressed;

        /// <summary>鼠标键位图：bit0 左 / bit1 右 / bit2 中（回放按同样的按下状态还原）。</summary>
        public byte MouseButtons;

        /// <summary>滚轮格数（本帧累计）。</summary>
        public int Wheel;

        /// <summary>本帧视角增量（弧度；与引擎把 PlayerInput.Look 加进 lookAngles 的量一致）。</summary>
        public float LookDeltaX;
        public float LookDeltaY;

        /// <summary>移动意图（-1..1，已归一化）—— 只读参考，回放靠按键还原。</summary>
        public float MoveX;
        public float MoveY;
        public float MoveZ;

        /// <summary>意图标志位（诊断/显示用；回放不直接使用）。</summary>
        public bool Jump;
        public bool Dig;
        public bool Hit;
        public bool Aim;
        public bool Interact;
        public bool Drop;
        public bool ToggleInventory;
        public bool ToggleCrouch;
        public bool ToggleMount;
        public bool ToggleCreativeFly;

        /// <summary>本帧选择的快捷栏槽位；-1 表示没有。</summary>
        public int SelectSlot;

        public bool HasKeys
        {
            get { return (KeysHeld != null && KeysHeld.Length > 0)
                || (KeysPressed != null && KeysPressed.Length > 0); }
        }
    }

    /// <summary>
    /// 动作包的逐帧轨道（`tracks/input.bin`），格式 v1。
    ///
    /// 设计要点：
    ///   · **自描述**：文件头带键名表，帧里只存索引 —— 于是格式不依赖游戏枚举的取值顺序
    ///     （换版本/换平台也不会把 W 读成 A），人工用十六进制看也能对上名字；
    ///   · **定长帧 + 变长按键**：常用帧约 30 字节；没人按键时更小（1043 帧 ≈ 30 KB）；
    ///   · 小端、无对齐填充，读不出来就明确报错（绝不"半个包也当好的"）。
    /// </summary>
    public static class ScatTrack
    {
        /// <summary>轨道文件魔数（8 字节 ASCII）。</summary>
        public const string Magic = "SCATIN01";

        public const int FormatVersion = 1;

        /// <summary>单帧最大字节（读的时候用来防"长度字段被写坏"导致的巨量分配）。</summary>
        public const int MaxFrameBytes = 512;

        public const int MaxKeysPerFrame = 16;

        private const int FlagJump = 1 << 0;
        private const int FlagDig = 1 << 1;
        private const int FlagHit = 1 << 2;
        private const int FlagAim = 1 << 3;
        private const int FlagInteract = 1 << 4;
        private const int FlagDrop = 1 << 5;
        private const int FlagToggleInventory = 1 << 6;
        private const int FlagToggleCrouch = 1 << 7;
        private const int FlagToggleMount = 1 << 8;
        private const int FlagToggleCreativeFly = 1 << 9;
        private const int FlagHasSlot = 1 << 10;
        private const int FlagHasWheel = 1 << 11;
        private const int FlagHasLook = 1 << 12;

        /// <summary>键名表：录制时出现过的键（索引 = 帧里存的那一个字节）。</summary>
        public sealed class Track
        {
            public readonly List<string> KeyNames = new List<string>();
            public readonly List<RecordingFrame> Frames = new List<RecordingFrame>();

            public int FrameCount
            {
                get { return Frames.Count; }
            }

            public double DurationSeconds
            {
                get
                {
                    double total = 0.0;
                    for (int i = 0; i < Frames.Count; i++)
                        total += Frames[i].DeltaMs / 1000.0;
                    return total;
                }
            }

            /// <summary>轨道里出现过的所有键名（供校验/显示）。</summary>
            public List<string> AllKeyNames()
            {
                var names = new List<string>(KeyNames);
                names.Sort(StringComparer.Ordinal);
                return names;
            }

            public int KeyIndexOf(string name)
            {
                for (int i = 0; i < KeyNames.Count; i++)
                {
                    if (string.Equals(KeyNames[i], name, StringComparison.Ordinal))
                        return i;
                }
                return -1;
            }

            public int EnsureKey(string name)
            {
                int existing = KeyIndexOf(name);
                if (existing >= 0)
                    return existing;
                if (KeyNames.Count >= 255)
                    return -1;
                KeyNames.Add(name);
                return KeyNames.Count - 1;
            }
        }

        // ---------------------------------------------------------------- 写

        /// <summary>把轨道写成 `tracks/input.bin` 的字节。</summary>
        public static byte[] ToBytes(Track track)
        {
            if (track == null)
                throw new ArgumentNullException(nameof(track));

            using (var stream = new MemoryStream())
            {
                var writer = new BinaryWriter(stream, new UTF8Encoding(false));

                byte[] magic = Encoding.ASCII.GetBytes(Magic);
                writer.Write(magic, 0, magic.Length);
                writer.Write((ushort)FormatVersion);
                writer.Write((uint)track.FrameCount);
                writer.Write((byte)Math.Min(255, track.KeyNames.Count));
                for (int i = 0; i < track.KeyNames.Count && i < 255; i++)
                {
                    byte[] name = Encoding.UTF8.GetBytes(track.KeyNames[i] ?? string.Empty);
                    writer.Write((byte)Math.Min(255, name.Length));
                    writer.Write(name, 0, Math.Min(255, name.Length));
                }

                for (int i = 0; i < track.Frames.Count; i++)
                    WriteFrame(writer, track.Frames[i]);

                writer.Flush();
                return stream.ToArray();
            }
        }

        private static void WriteFrame(BinaryWriter writer, RecordingFrame frame)
        {
            writer.Write((ushort)Clamp(frame.DeltaMs, 0, 65535));

            int flags = 0;
            if (frame.Jump) flags |= FlagJump;
            if (frame.Dig) flags |= FlagDig;
            if (frame.Hit) flags |= FlagHit;
            if (frame.Aim) flags |= FlagAim;
            if (frame.Interact) flags |= FlagInteract;
            if (frame.Drop) flags |= FlagDrop;
            if (frame.ToggleInventory) flags |= FlagToggleInventory;
            if (frame.ToggleCrouch) flags |= FlagToggleCrouch;
            if (frame.ToggleMount) flags |= FlagToggleMount;
            if (frame.ToggleCreativeFly) flags |= FlagToggleCreativeFly;
            if (frame.SelectSlot >= 0) flags |= FlagHasSlot;
            if (frame.Wheel != 0) flags |= FlagHasWheel;
            if (frame.LookDeltaX != 0f || frame.LookDeltaY != 0f) flags |= FlagHasLook;
            writer.Write((ushort)flags);

            writer.Write(Quantize(frame.MoveX));
            writer.Write(Quantize(frame.MoveY));
            writer.Write(Quantize(frame.MoveZ));
            writer.Write(frame.LookDeltaX);
            writer.Write(frame.LookDeltaY);
            writer.Write(frame.MouseButtons);
            writer.Write((sbyte)Clamp(frame.SelectSlot, -1, 127));
            writer.Write((sbyte)Clamp(frame.Wheel, -128, 127));

            byte[] held = frame.KeysHeld ?? EmptyKeys;
            writer.Write((byte)Math.Min(MaxKeysPerFrame, held.Length));
            for (int i = 0; i < held.Length && i < MaxKeysPerFrame; i++)
                writer.Write(held[i]);

            byte[] pressed = frame.KeysPressed ?? EmptyKeys;
            writer.Write((byte)Math.Min(MaxKeysPerFrame, pressed.Length));
            for (int i = 0; i < pressed.Length && i < MaxKeysPerFrame; i++)
                writer.Write(pressed[i]);
        }

        private static readonly byte[] EmptyKeys = new byte[0];

        private static sbyte Quantize(float value)
        {
            float scaled = value * 127f;
            if (scaled > 127f) scaled = 127f;
            if (scaled < -127f) scaled = -127f;
            return (sbyte)Math.Round(scaled);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return value < minimum ? minimum : (value > maximum ? maximum : value);
        }

        // ---------------------------------------------------------------- 读

        /// <summary>
        /// 读轨道。任何结构问题都通过 <paramref name="error"/> 返回（不抛异常），
        /// 调用方据此把它当成"这个动作包不可回放"。
        /// </summary>
        public static bool TryParse(byte[] bytes, out Track track, out string error)
        {
            track = null;
            error = null;

            if (bytes == null || bytes.Length < 8 + 2 + 4 + 1)
            {
                error = "track data is empty or truncated";
                return false;
            }

            try
            {
                using (var stream = new MemoryStream(bytes, writable: false))
                {
                    var reader = new BinaryReader(stream, Encoding.UTF8);

                    byte[] magic = reader.ReadBytes(Magic.Length);
                    if (Encoding.ASCII.GetString(magic) != Magic)
                    {
                        error = "track magic mismatch (expected " + Magic + ")";
                        return false;
                    }

                    int version = reader.ReadUInt16();
                    if (version != FormatVersion)
                    {
                        error = "unsupported track version " + version
                            + " (this build reads " + FormatVersion + ")";
                        return false;
                    }

                    uint frameCount = reader.ReadUInt32();
                    var result = new Track();

                    int keyCount = reader.ReadByte();
                    for (int i = 0; i < keyCount; i++)
                    {
                        int length = reader.ReadByte();
                        byte[] name = reader.ReadBytes(length);
                        if (name.Length != length)
                        {
                            error = "key name table is truncated";
                            return false;
                        }
                        result.KeyNames.Add(Encoding.UTF8.GetString(name));
                    }

                    for (uint i = 0; i < frameCount; i++)
                    {
                        RecordingFrame frame;
                        if (!TryReadFrame(reader, out frame, out error))
                            return false;
                        result.Frames.Add(frame);
                    }

                    track = result;
                    return true;
                }
            }
            catch (EndOfStreamException)
            {
                error = "track data ends in the middle of a frame";
                return false;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        private static bool TryReadFrame(BinaryReader reader, out RecordingFrame frame, out string error)
        {
            frame = default(RecordingFrame);
            error = null;

            if (reader.BaseStream.Length - reader.BaseStream.Position < 2 + 2 + 3 + 8 + 1 + 1 + 1 + 2)
            {
                error = "frame header is truncated";
                return false;
            }

            int deltaMs = reader.ReadUInt16();
            int flags = reader.ReadUInt16();
            sbyte moveX = reader.ReadSByte();
            sbyte moveY = reader.ReadSByte();
            sbyte moveZ = reader.ReadSByte();
            float lookX = reader.ReadSingle();
            float lookY = reader.ReadSingle();
            byte mouse = reader.ReadByte();
            sbyte slot = reader.ReadSByte();
            sbyte wheel = reader.ReadSByte();

            int heldCount = reader.ReadByte();
            if (heldCount > MaxKeysPerFrame)
            {
                error = "frame declares " + heldCount + " held keys (limit " + MaxKeysPerFrame + ")";
                return false;
            }
            byte[] held = reader.ReadBytes(heldCount);
            if (held.Length != heldCount)
            {
                error = "held key list is truncated";
                return false;
            }

            int pressedCount = reader.ReadByte();
            if (pressedCount > MaxKeysPerFrame)
            {
                error = "frame declares " + pressedCount + " pressed keys (limit "
                    + MaxKeysPerFrame + ")";
                return false;
            }
            byte[] pressed = reader.ReadBytes(pressedCount);
            if (pressed.Length != pressedCount)
            {
                error = "pressed key list is truncated";
                return false;
            }

            frame = new RecordingFrame
            {
                DeltaMs = deltaMs,
                KeysHeld = held,
                KeysPressed = pressed,
                MouseButtons = mouse,
                Wheel = wheel,
                LookDeltaX = lookX,
                LookDeltaY = lookY,
                MoveX = moveX / 127f,
                MoveY = moveY / 127f,
                MoveZ = moveZ / 127f,
                Jump = (flags & FlagJump) != 0,
                Dig = (flags & FlagDig) != 0,
                Hit = (flags & FlagHit) != 0,
                Aim = (flags & FlagAim) != 0,
                Interact = (flags & FlagInteract) != 0,
                Drop = (flags & FlagDrop) != 0,
                ToggleInventory = (flags & FlagToggleInventory) != 0,
                ToggleCrouch = (flags & FlagToggleCrouch) != 0,
                ToggleMount = (flags & FlagToggleMount) != 0,
                ToggleCreativeFly = (flags & FlagToggleCreativeFly) != 0,
                SelectSlot = (flags & FlagHasSlot) != 0 ? slot : -1
            };
            return true;
        }

        /// <summary>把某一帧的键索引翻成键名（回放与显示用）。</summary>
        public static List<string> KeyNamesOf(Track track, byte[] indices)
        {
            var names = new List<string>();
            if (track == null || indices == null)
                return names;

            for (int i = 0; i < indices.Length; i++)
            {
                int index = indices[i];
                if (index >= 0 && index < track.KeyNames.Count)
                    names.Add(track.KeyNames[index]);
                else
                    names.Add("<key#" + index + ">");
            }
            return names;
        }
    }
}
