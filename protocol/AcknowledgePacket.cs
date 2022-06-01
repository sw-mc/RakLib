using SkyWing.Binary;

namespace RakLib.protocol;

public abstract class AcknowledgePacket : Packet {

	private const int RECORD_TYPE_RANGE = 0;
	private const int RECORD_TYPE_SINGLE = 1;

	public int[] Packets { get; set; } = Array.Empty<int>();

	protected override void EncodePayload(PacketSerializer buffer) {
		var payload = new BinaryStream(new MemoryStream());
		Array.Sort(Packets);
		var count = Packets.Length;
		short records = 0;

		if (count > 0) {
			var pointer = 1;
			var start = Packets[0];
			var end = Packets[0];

			while (pointer < count) {
				var current = Packets[pointer++];
				var diff = current - end;
				if (diff == 1) {
					end = current;
				}
				else if (diff > 1) {
					if (start == end) {
						payload.WriteByte(RECORD_TYPE_SINGLE);
						payload.WriteLInt24(start);
						start = end = current;
					}
					else {
						payload.WriteByte(RECORD_TYPE_RANGE);
						payload.WriteLInt24(start);
						payload.WriteLInt24(end);
						start = end = current;
					}
					++records;
				}
			}

			if (start == end) {
				payload.WriteByte(RECORD_TYPE_SINGLE);
				payload.WriteLInt24(start);
			}
			else {
				payload.WriteByte(RECORD_TYPE_RANGE);
				payload.WriteLInt24(start);
				payload.WriteLInt24(end);
			}
			++records;
		}
		
		buffer.WriteShort(records);
		buffer.WriteBytes(new BinaryReader(payload.Buffer).ReadBytes((int) payload.Buffer.Length));
	}

	protected override void DecodePayload(PacketSerializer buffer) {
		var count = buffer.ReadShort();
		var packets = new int[count];
		var cnt = 0;
		for (var i = 0; i < count; ++i) {
			var type = buffer.ReadByte();
			switch (type) {
				case RECORD_TYPE_SINGLE:
					packets[cnt++] = buffer.ReadLInt24();
					break;
				case RECORD_TYPE_RANGE: {
					var start = buffer.ReadLInt24();
					var end = buffer.ReadLInt24();
					if((end - start) > 512){
						end = start + 512;
					}
					for (var j = start; j <= end; ++j) {
						packets[cnt++] = j;
					}
					break;
				}
			}
		}
	}
}

public class Ack : AcknowledgePacket{

	public override int GetPid() {
		return 0xc0;
	}
}

public class Nack : AcknowledgePacket{

	public override int GetPid() {
		return 0xa0;
	}
}