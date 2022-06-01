using SkyWing.RakLib;

namespace RakLib.protocol;

public abstract class ConnectedPacket : Packet {
	
}

public class ConnectPing : ConnectedPacket {
	
	public long SendPingTime { get; set; }

	public override int GetPid() {
		return MessageIdentifiers.ID_CONNECTED_PING;
	}

	public static ConnectPing Create(long sendPingTime) {
		var result = new ConnectPing();
		result.SendPingTime = sendPingTime;
		return result;
	}

	protected override void EncodePayload(PacketSerializer buffer) {
		buffer.WriteLong(SendPingTime);
	}

	protected override void DecodePayload(PacketSerializer buffer) {
		SendPingTime = buffer.ReadLong();
	}
}

public class ConnectPong : ConnectedPacket {
	
	public long SendPingTime { get; set; }
	public long SendPongTime { get; set; }

	public override int GetPid() {
		return MessageIdentifiers.ID_CONNECTED_PONG;
	}
	
	public static ConnectPong Create(long sendPingTime, long sendPongTime) {
		var result = new ConnectPong();
		result.SendPingTime = sendPingTime;
		result.SendPongTime = sendPongTime;
		return result;
	}

	protected override void EncodePayload(PacketSerializer buffer) {
		buffer.WriteLong(SendPingTime);
		buffer.WriteLong(SendPongTime);
	}

	protected override void DecodePayload(PacketSerializer buffer) {
		SendPingTime = buffer.ReadLong();
		SendPongTime = buffer.ReadLong();
	}
}

public class ConnectionRequest : ConnectedPacket {
	
	public long ClientId { get; set; }
	public long SendPingTime { get; set; }
	public bool UseSecurity { get; set; }

	public override int GetPid() {
		return MessageIdentifiers.ID_CONNECTION_REQUEST;
	}

	protected override void EncodePayload(PacketSerializer buffer) {
		buffer.WriteLong(ClientId);
		buffer.WriteLong(SendPingTime);
		buffer.WriteBool(UseSecurity);
	}

	protected override void DecodePayload(PacketSerializer buffer) {
		ClientId = buffer.ReadLong();
		SendPingTime = buffer.ReadLong();
		UseSecurity = buffer.ReadByte() != 0;
	}
}

public class ConnectionRequestAccepted : ConnectedPacket {
	
	public InternetAddress Address { get; set; }
	public InternetAddress[] SystemAddresses { get; set; }
	
	public long SendPingTime { get; set; }
	public long SendPongTime { get; set; }

	public override int GetPid() {
		return MessageIdentifiers.ID_CONNECTION_REQUEST_ACCEPTED;
	}
	
	public static ConnectionRequestAccepted Create(InternetAddress address, InternetAddress[] systemAddresses, long sendPingTime, long sendPongTime) {
		var result = new ConnectionRequestAccepted();
		result.Address = address;
		result.SystemAddresses = systemAddresses;
		result.SendPingTime = sendPingTime;
		result.SendPongTime = sendPongTime;
		return result;
	}

	public ConnectionRequestAccepted() {
		SystemAddresses = new[] {new InternetAddress("127.0.0.1", 0, InternetVersion.Ipv4)};
	}

	protected override void EncodePayload(PacketSerializer buffer) {
		buffer.WriteAddress(Address);
		buffer.WriteShort(0);

		var dummy = new InternetAddress("0.0.0.0", 0, InternetVersion.Ipv4);
		for (var i = 0; i < RakLib.SYSTEM_ADDRESS_COUNT; ++i) {
			buffer.WriteAddress(i < SystemAddresses.Length ? SystemAddresses[i] : dummy);
		}
		buffer.WriteLong(SendPingTime);
		buffer.WriteLong(SendPongTime);
	}

	protected override void DecodePayload(PacketSerializer buffer) {
		Address = buffer.ReadAddress();
		buffer.ReadShort();
		SystemAddresses = new InternetAddress[RakLib.SYSTEM_ADDRESS_COUNT];
		for (var i = 0; i < RakLib.SYSTEM_ADDRESS_COUNT; ++i) {
			SystemAddresses[i] = buffer.ReadAddress();
		}
		SendPingTime = buffer.ReadLong();
		SendPongTime = buffer.ReadLong();
	}
}

public class DisconnectionNotification : ConnectedPacket {
	
	public override int GetPid() {
		return MessageIdentifiers.ID_DISCONNECTION_NOTIFICATION;
	}

	protected override void EncodePayload(PacketSerializer buffer) {
		//NOOP
	}

	protected override void DecodePayload(PacketSerializer buffer) {
		//NOOP
	}
}

public class NewIncomingConnection : ConnectedPacket {
	
	public InternetAddress Address { get; set; }
	public InternetAddress[] SystemAddresses { get; set; }
	
	public long SendPingTime { get; set; }
	public long SendPongTime { get; set; }


	public override int GetPid() {
		return MessageIdentifiers.ID_NEW_INCOMING_CONNECTION;
	}

	protected override void EncodePayload(PacketSerializer buffer) {
		buffer.WriteAddress(Address);
		foreach (var address in SystemAddresses) {
			buffer.WriteAddress(address);
		}
		buffer.WriteLong(SendPingTime);
		buffer.WriteLong(SendPongTime);
	}

	protected override void DecodePayload(PacketSerializer buffer) {
		Address = buffer.ReadAddress();
		SystemAddresses = new InternetAddress[RakLib.SYSTEM_ADDRESS_COUNT];
		for (var i = 0; i < RakLib.SYSTEM_ADDRESS_COUNT; ++i) {
			SystemAddresses[i] = buffer.ReadAddress();
		}
		SendPingTime = buffer.ReadLong();
		SendPongTime = buffer.ReadLong();
	}
}


