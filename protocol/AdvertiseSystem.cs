namespace SkyWing.RakLib.Protocol; 

public class AdvertiseSystem : Packet {
	
	public string ServerName { get; set; }

	public override int GetPid() {
		return MessageIdentifiers.ID_ADVERTISE_SYSTEM;
	}

	protected override void EncodePayload(PacketSerializer buffer) {
		buffer.WriteString(ServerName);
	}

	protected override void DecodePayload(PacketSerializer buffer) {
		ServerName = buffer.ReadString();
	}
}