using SkyWing.Binary;

namespace SkyWing.RakLib.Protocol;

public abstract class OfflineMessage : Packet {
	
	private static readonly byte[] magic = {
		0x00, 0xff, 0xff, 0x00, 0xfe, 0xfe, 0xfe, 0xfe, 0xfd, 0xfd, 0xfd, 0xfd, 0x12, 0x34, 0x56, 0x78
	};

	protected byte[]? Magic = null;

	protected void ReadMagic(BinaryStream buffer) {
		Magic = buffer.ReadBytes(16);
	}
	
	protected void WriteMagic(BinaryStream buffer) {
		buffer.WriteBytes(magic);
	}

	public bool IsValid() {
        return Magic != null && Magic.SequenceEqual(magic!);
	}
}

public class IncompatibleProtocolVersion : OfflineMessage {
	
	public int ProtocolVersion { get; set; }
	public long ServerId { get; set; }

	public override int GetPid() {
		return MessageIdentifiers.ID_INCOMPATIBLE_PROTOCOL_VERSION;
	}

	public static IncompatibleProtocolVersion Create(int protocol, long server) {
		return new IncompatibleProtocolVersion {
			ProtocolVersion = protocol,
			ServerId = server
		};
	}

	protected override void EncodePayload(PacketSerializer buffer) {
		buffer.WriteByte((byte) ProtocolVersion);
		WriteMagic(buffer);
		buffer.WriteLong(ServerId);
	}

	protected override void DecodePayload(PacketSerializer buffer) {
		ProtocolVersion = buffer.ReadByte();
		ReadMagic(buffer);
		ServerId = buffer.ReadLong();
	}
	
}

public class OpenConnectionReply1 : OfflineMessage {
	
	public long ServerId { get; set; }
	public bool ServerSecurity { get; set; }
	public short MtuSize { get; set; }

	public override int GetPid() {
		return MessageIdentifiers.ID_OPEN_CONNECTION_REPLY_1;
	}
	
	public static OpenConnectionReply1 Create(long serverId, bool security, short mtu) {
		return new OpenConnectionReply1 {
			ServerId = serverId,
			ServerSecurity = security,
			MtuSize = mtu
		};
	}

	protected override void EncodePayload(PacketSerializer buffer) {
		WriteMagic(buffer);
		buffer.WriteLong((byte) ServerId);
		buffer.WriteByte((byte) (ServerSecurity ? 1 : 0));
		buffer.WriteShort(MtuSize);
	}

	protected override void DecodePayload(PacketSerializer buffer) {
		ReadMagic(buffer);
		ServerId = buffer.ReadLong();
		ServerSecurity = buffer.ReadByte() == 1;
		MtuSize = buffer.ReadShort();
	}
	
}

public class OpenConnectionReply2 : OfflineMessage {
	
	public long ServerId { get; set; }
	public InternetAddress ClientAddress { get; set; }
	public short MtuSize { get; set; }
	public bool ServerSecurity { get; set; }

	public override int GetPid() {
		return MessageIdentifiers.ID_OPEN_CONNECTION_REPLY_2;
	}
	
	public static OpenConnectionReply2 Create(long serverId, InternetAddress clientAddress, short mtu, bool security) {
		return new OpenConnectionReply2 {
			ServerId = serverId,
			ClientAddress = clientAddress,
			ServerSecurity = security,
			MtuSize = mtu
		};
	}

	protected override void EncodePayload(PacketSerializer buffer) {
		WriteMagic(buffer);
		buffer.WriteLong((byte) ServerId);
		buffer.WriteAddress(ClientAddress);
		buffer.WriteShort(MtuSize);
		buffer.WriteByte((byte) (ServerSecurity ? 1 : 0));
	}

	protected override void DecodePayload(PacketSerializer buffer) {
		ReadMagic(buffer);
		ServerId = buffer.ReadLong();
		ClientAddress = buffer.ReadAddress();
		MtuSize = buffer.ReadShort();
		ServerSecurity = buffer.ReadByte() == 1;
	}
	
}

public class OpenConnectionRequest1 : OfflineMessage {

	public int Protocol { get; set; } = RakLib.DEFAULT_PROTOCOL_VERSION;
	public short MtuSize { get; set; }
	
	public override int GetPid() {
		return MessageIdentifiers.ID_OPEN_CONNECTION_REQUEST_1;
	}

	protected override void EncodePayload(PacketSerializer buffer) {
		WriteMagic(buffer);
		buffer.WriteByte((byte) Protocol);
		buffer.WriteShort(MtuSize);
		
		var count = MtuSize - buffer.Length;
		var bytes = new byte[count];
		for (var i = 0; i < count; i++) {
			bytes[i] = 0x00;
		}
		buffer.WriteBytes(bytes);
	}

	protected override void DecodePayload(PacketSerializer buffer) {
		ReadMagic(buffer);
		Protocol = buffer.ReadByte();
		MtuSize = buffer.ReadShort();
		buffer.GetRemainingBytes();
	}
	
}

public class OpenConnectionRequest2 : OfflineMessage {
	
	public InternetAddress ServerAddress { get; set; }
	public short MtuSize { get; set; }
	public long ClientId { get; set; }
	
	public override int GetPid() {
		return MessageIdentifiers.ID_OPEN_CONNECTION_REQUEST_2;
	}

	protected override void EncodePayload(PacketSerializer buffer) {
		WriteMagic(buffer);
		buffer.WriteAddress(ServerAddress);
		buffer.WriteShort(MtuSize);
		buffer.WriteLong((byte) ClientId);
	}

	protected override void DecodePayload(PacketSerializer buffer) {
		ReadMagic(buffer);
		ServerAddress = buffer.ReadAddress();
		MtuSize = buffer.ReadShort();
		ClientId = buffer.ReadLong();
	}

}

public class UnconnectedPing : OfflineMessage {
	
	public long SendPingTime { get; set; }
	public long ClientId { get; set; }
	
	public override int GetPid() {
		return MessageIdentifiers.ID_UNCONNECTED_PING;
	}

	protected override void EncodePayload(PacketSerializer buffer) {
		buffer.WriteLong((byte) SendPingTime);
		WriteMagic(buffer);
		buffer.WriteLong((byte) ClientId);
	}

	protected override void DecodePayload(PacketSerializer buffer) {
		SendPingTime = buffer.ReadLong();
		ReadMagic(buffer);
		ClientId = buffer.ReadLong();
	}
}

public class UnconnectedPingOpenConnections : UnconnectedPing {
	
	public override int GetPid() {
		return MessageIdentifiers.ID_UNCONNECTED_PING_OPEN_CONNECTIONS;
	}
	
}

public class UnconnectedPong : OfflineMessage {
	
	public long SendPingTime { get; set; }
	public long ServerId { get; set; }
	public string ServerName { get; set; }
	
	public override int GetPid() {
		return MessageIdentifiers.ID_UNCONNECTED_PONG;
	}

	public static UnconnectedPong Create(long sendPingTime, long serverId, string serverName) {
		return new UnconnectedPong {
			SendPingTime = sendPingTime,
			ServerId = serverId,
			ServerName = serverName
		};
	}

	protected override void EncodePayload(PacketSerializer buffer) {
		buffer.WriteLong(SendPingTime);
		buffer.WriteLong(ServerId);
		WriteMagic(buffer);
		buffer.WriteString(ServerName);
	}
	
	protected override void DecodePayload(PacketSerializer buffer) {
		SendPingTime = buffer.ReadLong();
		ServerId = buffer.ReadLong();
		ReadMagic(buffer);
		ServerName = buffer.ReadString();
	}
}
