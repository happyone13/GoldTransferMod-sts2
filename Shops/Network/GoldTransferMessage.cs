using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace GoldTransferMod.Shops.Network;

public struct GoldTransferMessage : INetMessage, IPacketSerializable
{
    public ulong TargetId;
    public int Amount;

    public bool ShouldBroadcast => true;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Debug;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(TargetId);
        writer.WriteInt(Amount);
    }

    public void Deserialize(PacketReader reader)
    {
        TargetId = reader.ReadULong();
        Amount = reader.ReadInt();
    }
}
