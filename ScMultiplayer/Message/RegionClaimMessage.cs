using Comms;
using System;
using System.Collections.Generic;
using System.IO;

namespace ScMultiplayer
{
    public enum RegionClaimStage
    {
        /// <summary>主机 → 客户端：完整快照（加入时随世界快照下发，或响应 RequestSync）。</summary>
        Full = 0,
        /// <summary>主机 → 客户端：单块增量（Add / Replace / Remove）。</summary>
        Delta = 1,
        /// <summary>客户端 → 主机：请求重发完整快照（检测到序号缺口时）。</summary>
        RequestSync = 2
    }

    public enum RegionClaimOperation
    {
        Add = 0,
        Remove = 1,
        Replace = 2
    }

    /// <summary>
    /// 《玩家领地》P2：领地区域的**同步报文**（世界观数据，主机权威、客户端只读副本）。
    ///
    /// 设计稿 §1：加入时随世界快照一次性下发（<see cref="RegionClaimStage.Full"/>），
    /// 运行期广播增量（<see cref="RegionClaimStage.Delta"/>），**带单调序号，按序号应用**；
    /// 客户端发现序号缺口时回 <see cref="RegionClaimStage.RequestSync"/> 让主机补发。
    ///
    /// Source: Mod/ScMultiplayer/Message/GamePakWorldReadyMessage.cs（消息写法）
    /// Source: Mod/ScMultiplayer/Message/Message.cs（显式 wire id 注册，append-only）
    /// </summary>
    [Serializable]
    public class RegionClaimMessage : Message
    {
        // 协议防御：读数前先卡住上限，避免坏包/恶意包让对端按超大计数分配内存。
        private const int MaximumClaimRecordsPerMessage = 512;
        private const int MaximumOwnerRecordsPerClaim = 64;

        public RegionClaimStage Stage;
        /// <summary>主机侧的单调递增序号（每次改动 +1）。</summary>
        public long Sequence;
        /// <summary>下一块领地的编号（客户端副本跟着更新，便于显示）。</summary>
        public int NextId = 1;
        public RegionClaimOperation Operation;
        /// <summary>Full：全部领地；Delta+Add/Replace：单块。</summary>
        public List<RegionClaim> Claims = new List<RegionClaim>();
        /// <summary>Delta+Remove 用。</summary>
        public int RemoveId;

        public RegionClaimMessage()
        {
        }

        public static RegionClaimMessage CreateFull(long sequence, int nextId,
            IEnumerable<RegionClaim> claims)
        {
            var message = new RegionClaimMessage
            {
                Stage = RegionClaimStage.Full,
                Sequence = sequence,
                NextId = nextId
            };
            if (claims != null)
            {
                foreach (RegionClaim claim in claims)
                    message.Claims.Add(claim);
            }
            return message;
        }

        public static RegionClaimMessage CreateDelta(long sequence, int nextId,
            RegionClaimOperation operation, RegionClaim claim, int removeId)
        {
            var message = new RegionClaimMessage
            {
                Stage = RegionClaimStage.Delta,
                Sequence = sequence,
                NextId = nextId,
                Operation = operation,
                RemoveId = removeId
            };
            if (claim != null)
                message.Claims.Add(claim);
            return message;
        }

        public static RegionClaimMessage CreateRequestSync() =>
            new RegionClaimMessage { Stage = RegionClaimStage.RequestSync };

        protected override void Read(SuReader reader)
        {
            Stage = (RegionClaimStage)reader.ReadPackedInt32();
            Sequence = reader.ReadInt64();
            NextId = reader.ReadPackedInt32();
            Operation = (RegionClaimOperation)reader.ReadPackedInt32();
            RemoveId = reader.ReadPackedInt32();
            Claims.Clear();
            int count = reader.ReadPackedInt32();
            if (count < 0 || count > MaximumClaimRecordsPerMessage)
                throw new InvalidDataException("Invalid region claim count: " + count);
            for (int i = 0; i < count; i++)
                Claims.Add(ReadClaim(reader));
        }

        protected override void Write(SuWriter writer)
        {
            writer.WritePackedInt32((int)Stage);
            writer.WriteInt64(Sequence);
            writer.WritePackedInt32(NextId);
            writer.WritePackedInt32((int)Operation);
            writer.WritePackedInt32(RemoveId);
            int count = Claims?.Count ?? 0;
            writer.WritePackedInt32(count);
            for (int i = 0; i < count; i++)
                WriteClaim(writer, Claims[i]);
        }

        private static void WriteClaim(SuWriter writer, RegionClaim claim)
        {
            if (claim == null)
                claim = new RegionClaim();
            writer.WritePackedInt32(claim.Id);
            writer.WritePackedInt32(claim.MinX);
            writer.WritePackedInt32(claim.MinY);
            writer.WritePackedInt32(claim.MinZ);
            writer.WritePackedInt32(claim.MaxX);
            writer.WritePackedInt32(claim.MaxY);
            writer.WritePackedInt32(claim.MaxZ);
            writer.WriteString(claim.CreatedUtc ?? string.Empty);
            writer.WriteString(claim.Name ?? string.Empty);
            writer.WriteString(claim.Flags ?? string.Empty);
            int owners = claim.Owners?.Count ?? 0;
            writer.WritePackedInt32(owners);
            for (int i = 0; i < owners; i++)
            {
                RegionClaimOwner owner = claim.Owners[i];
                writer.WriteString(owner?.UserId ?? string.Empty);
                writer.WriteString(owner?.Name ?? string.Empty);
            }
        }

        private static RegionClaim ReadClaim(SuReader reader)
        {
            var claim = new RegionClaim
            {
                Id = reader.ReadPackedInt32(),
                MinX = reader.ReadPackedInt32(),
                MinY = reader.ReadPackedInt32(),
                MinZ = reader.ReadPackedInt32(),
                MaxX = reader.ReadPackedInt32(),
                MaxY = reader.ReadPackedInt32(),
                MaxZ = reader.ReadPackedInt32(),
                CreatedUtc = reader.ReadString(),
                Name = reader.ReadString(),
                Flags = reader.ReadString()
            };
            int owners = reader.ReadPackedInt32();
            if (owners < 0 || owners > MaximumOwnerRecordsPerClaim)
                throw new InvalidDataException("Invalid region owner count: " + owners);
            for (int i = 0; i < owners; i++)
            {
                claim.Owners.Add(new RegionClaimOwner
                {
                    UserId = reader.ReadString(),
                    Name = reader.ReadString()
                });
            }
            return claim;
        }
    }
}
