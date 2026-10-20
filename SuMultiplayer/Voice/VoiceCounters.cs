using System;
using System.Text;

namespace SuMultiplayer.Voice
{
    /// <summary>
    /// 语音计数（任务要求的口径，全部集中在这里；模块只做 +1 / 加字节）。
    /// <see cref="BytesPerSecond"/> 由模块按 1 秒窗口更新，日志每 10 s 打一行。
    /// 无静态可变状态。
    /// </summary>
    public sealed class VoiceCounters
    {
        public long CaptureFrames { get; private set; }
        public long CaptureDropped { get; private set; }
        public long Sent { get; private set; }
        public long Recv { get; private set; }
        public long Relayed { get; private set; }
        public long Played { get; private set; }
        public long SilenceSkipped { get; private set; }
        public long LateDropped { get; private set; }
        public long EncodeFail { get; private set; }
        public long DecodeFail { get; private set; }
        public long Malformed { get; private set; }
        public long SendFailures { get; private set; }
        public long QueueDropped { get; private set; }
        public long NoDevice { get; private set; }
        public long NoPermission { get; private set; }
        public long NoPlayback { get; private set; }
        /// <summary>Android 录音权限：发出请求的次数（每次用户把语音切 ON 且未授权时 +1）。</summary>
        public long PermissionRequested { get; private set; }
        /// <summary>录音权限已授权（本会话）。</summary>
        public long PermissionGranted { get; private set; }
        /// <summary>录音权限被拒绝（可以再请求；不是永久拒绝）。</summary>
        public long PermissionDenied { get; private set; }
        /// <summary>录音权限被永久拒绝（“不再询问”）。</summary>
        public long PermissionPermanentlyDenied { get; private set; }
        /// <summary>为恢复权限而打开系统应用设置页的次数。</summary>
        public long PermissionSettingsOpened { get; private set; }
        /// <summary>权限对象 / Activity 不可用，无法请求（明确失败，不弹窗）。</summary>
        public long PermissionUnavailable { get; private set; }
        public long StartTalk { get; private set; }
        public long EndTalk { get; private set; }
        public long DuplicateDropped { get; private set; }
        public long MutedDropped { get; private set; }
        public long GapSkipped { get; private set; }
        public long OverflowDropped { get; private set; }
        public long RelaySkippedMuted { get; private set; }
        public long RelaySkippedDisabled { get; private set; }
        public long ControlSent { get; private set; }
        public long PttBlocked { get; private set; }
        public long MuteRequests { get; private set; }
        public long MuteRejected { get; private set; }
        public long DirectionErrors { get; private set; }
        public long BytesSentTotal { get; private set; }
        public long BytesRecvTotal { get; private set; }
        public long PacketsSentLastSecond { get; private set; }
        public long PacketsRecvLastSecond { get; private set; }
        public long BytesPerSecond { get; private set; }
        public long SentBytesPerSecond { get; private set; }
        public long RecvBytesPerSecond { get; private set; }
        /// <summary>按格式字段拒绝的包 / 协商（未知档位、或与说话者声明的档位不一致）。</summary>
        public long FormatRejected { get; private set; }
        /// <summary>当前生效的编码档位（日志 / HUD 用；切换后下一拍更新）。</summary>
        public VoiceQuality Quality { get; private set; } = VoiceProfiles.Default;

        public void NoteCaptureFrame() => CaptureFrames++;

        public void NoteCaptureDropped(int count) => CaptureDropped += count;

        public void NoteSent(int wireBytes)
        {
            Sent++;
            BytesSentTotal += wireBytes;
        }

        public void NoteReceived(int wireBytes)
        {
            Recv++;
            BytesRecvTotal += wireBytes;
        }

        public void NoteRelayed(int wireBytes)
        {
            Relayed++;
            BytesSentTotal += wireBytes;
        }

        /// <summary>协商 / 名册 / 静音结果等控制消息：计入带宽，但不冒充语音包计数。</summary>
        public void NoteControl(int wireBytes)
        {
            ControlSent++;
            BytesSentTotal += wireBytes;
        }

        public void NotePlayed() => Played++;

        public void NoteSilenceSkipped() => SilenceSkipped++;

        /// <summary>PTT 模式未按下：帧被主动挡住（不是静音，单独计数）。</summary>
        public void NotePttBlocked() => PttBlocked++;

        public void NoteLateDropped() => LateDropped++;

        public void NoteLateDropped(long count) => LateDropped += count;

        public void NoteMutedDropped() => MutedDropped++;

        public void NoteOverflowBy(long count) => OverflowDropped += count;

        public void NoteDuplicateBy(long count) => DuplicateDropped += count;

        public void NoteEncodeFail() => EncodeFail++;

        public void NoteDecodeFail() => DecodeFail++;

        public void NoteMalformed() => Malformed++;

        public void NoteSendFailure() => SendFailures++;

        public void NoteQueueDropped() => QueueDropped++;

        public void NoteNoDevice() => NoDevice++;

        public void NoteNoPermission() => NoPermission++;

        public void NoteNoPlayback() => NoPlayback++;

        public void NotePermissionRequested() => PermissionRequested++;

        public void NotePermissionGranted() => PermissionGranted++;

        public void NotePermissionDenied() => PermissionDenied++;

        public void NotePermissionPermanentlyDenied() => PermissionPermanentlyDenied++;

        public void NotePermissionSettingsOpened() => PermissionSettingsOpened++;

        public void NotePermissionUnavailable() => PermissionUnavailable++;

        public void NoteStartTalk() => StartTalk++;

        public void NoteEndTalk() => EndTalk++;

        public void NoteDuplicate() => DuplicateDropped++;

        public void NoteGapSkipped(long count) => GapSkipped += count;

        public void NoteOverflow() => OverflowDropped++;

        public void NoteRelayMuted() => RelaySkippedMuted++;

        public void NoteRelayDisabled() => RelaySkippedDisabled++;

        public void NoteMuteRequest() => MuteRequests++;

        public void NoteMuteRejected() => MuteRejected++;

        public void NoteDirectionError() => DirectionErrors++;

        /// <summary>未知 / 不匹配的档位：整包（或整条协商）拒绝，绝不按默认档位解码。</summary>
        public void NoteFormatRejected() => FormatRejected++;

        /// <summary>记录当前档位（模块在构造 / 运行期切换时调用）。</summary>
        public void SetQuality(VoiceQuality quality) => Quality = quality;

        /// <summary>1 秒速率窗口（模块每 tick 调用；只在跨过 1000 ms 时更新）。</summary>
        public void UpdateRates(long nowMs, ref long windowStartMs, ref long windowSent,
            ref long windowRecv, ref long windowSentPackets, ref long windowRecvPackets)
        {
            if (windowStartMs == 0)
            {
                windowStartMs = nowMs;
                windowSent = BytesSentTotal;
                windowRecv = BytesRecvTotal;
                windowSentPackets = Sent + Relayed;
                windowRecvPackets = Recv;
                return;
            }
            long elapsed = nowMs - windowStartMs;
            if (elapsed < 1000) return;
            long sentDelta = BytesSentTotal - windowSent;
            long recvDelta = BytesRecvTotal - windowRecv;
            long packetsSent = Sent + Relayed;
            long packetsRecv = Recv;
            PacketsSentLastSecond = (packetsSent - windowSentPackets) * 1000 / elapsed;
            PacketsRecvLastSecond = (packetsRecv - windowRecvPackets) * 1000 / elapsed;
            SentBytesPerSecond = sentDelta * 1000 / elapsed;
            RecvBytesPerSecond = recvDelta * 1000 / elapsed;
            BytesPerSecond = (sentDelta + recvDelta) * 1000 / elapsed;
            windowStartMs = nowMs;
            windowSent = BytesSentTotal;
            windowRecv = BytesRecvTotal;
            windowSentPackets = packetsSent;
            windowRecvPackets = packetsRecv;
        }

        public string Describe()
        {
            var builder = new StringBuilder(256);
            builder.Append("voiceCaptureFrames=").Append(CaptureFrames);
            builder.Append(" voiceSent=").Append(Sent);
            builder.Append(" voiceRecv=").Append(Recv);
            builder.Append(" voiceRelayed=").Append(Relayed);
            builder.Append(" voicePlayed=").Append(Played);
            builder.Append(" voiceSilenceSkipped=").Append(SilenceSkipped);
            builder.Append(" voiceLateDropped=").Append(LateDropped);
            builder.Append(" voiceQuality=").Append(VoiceProfiles.Name(Quality));
            builder.Append(" voicePacketBytes=").Append(VoiceFormat.EncodedBytes(Quality));
            builder.Append(" voiceBytesPs=").Append(BytesPerSecond);
            builder.Append(" voiceEncodeFail=").Append(EncodeFail);
            builder.Append(" voiceNoDevice=").Append(NoDevice);
            builder.Append(" voiceNoPermission=").Append(NoPermission);
            builder.Append(" voicePermissionRequested=").Append(PermissionRequested);
            builder.Append(" voicePermissionGranted=").Append(PermissionGranted);
            builder.Append(" voicePermissionDenied=").Append(PermissionDenied);
            builder.Append(" voicePermissionPermanentlyDenied=").Append(PermissionPermanentlyDenied);
            builder.Append(" voicePermissionSettingsOpened=").Append(PermissionSettingsOpened);
            builder.Append(" voicePermissionUnavailable=").Append(PermissionUnavailable);
            return builder.ToString();
        }

        public string DescribeDetail()
        {
            var builder = new StringBuilder(384);
            builder.Append(Describe());
            builder.Append(" sentBytesPs=").Append(SentBytesPerSecond);
            builder.Append(" recvBytesPs=").Append(RecvBytesPerSecond);
            builder.Append(" bytesSent=").Append(BytesSentTotal);
            builder.Append(" bytesRecv=").Append(BytesRecvTotal);
            builder.Append(" sentPacketsPs=").Append(PacketsSentLastSecond);
            builder.Append(" recvPacketsPs=").Append(PacketsRecvLastSecond);
            builder.Append(" malformed=").Append(Malformed);
            builder.Append(" formatRejected=").Append(FormatRejected);
            builder.Append(" decodeFail=").Append(DecodeFail);
            builder.Append(" sendFailures=").Append(SendFailures);
            builder.Append(" mutedDropped=").Append(MutedDropped);
            builder.Append(" queueDropped=").Append(QueueDropped);
            builder.Append(" duplicate=").Append(DuplicateDropped);
            builder.Append(" gapSkipped=").Append(GapSkipped);
            builder.Append(" overflow=").Append(OverflowDropped);
            builder.Append(" relayMuted=").Append(RelaySkippedMuted);
            builder.Append(" relayDisabled=").Append(RelaySkippedDisabled);
            builder.Append(" controlSent=").Append(ControlSent);
            builder.Append(" pttBlocked=").Append(PttBlocked);
            builder.Append(" muteReq=").Append(MuteRequests);
            builder.Append(" muteRejected=").Append(MuteRejected);
            builder.Append(" startTalk=").Append(StartTalk);
            builder.Append(" endTalk=").Append(EndTalk);
            builder.Append(" directionErrors=").Append(DirectionErrors);
            builder.Append(" noPlayback=").Append(NoPlayback);
            return builder.ToString();
        }
    }
}
