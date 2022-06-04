using SkyWing.RakLib.Protocol;

namespace SkyWing.RakLib.Generic; 

public sealed class ReceiveReliabilityLayer{

    public const int WINDOW_SIZE = 2048;
    
	private readonly Action<EncapsulatedPacket> onRecv;
    
	private readonly Action<AcknowledgePacket> sendPacket;

	private int windowStart;
	private int windowEnd;
	private int highestSeqNumber = -1;

    private readonly Dictionary<int, int> ackQueue = new();
    private readonly Dictionary<int, int> nackQueue = new();

	private int reliableWindowStart;
	private int reliableWindowEnd;
    private readonly Dictionary<int, bool> reliableWindow = new();

    private readonly Dictionary<int, int> receiveOrderedIndex = new();
    private readonly Dictionary<int, int> receiveSequencedHighestIndex = new();
    private readonly Dictionary<int, Dictionary<int, EncapsulatedPacket?>> receiveOrderedPackets = new();

    private readonly Dictionary<int, Dictionary<int, EncapsulatedPacket?>> splitPackets = new();
    
	private readonly int maxSplitPacketPartCount;
	private readonly int maxConcurrentSplitPackets;

	/**
	 * @phpstan-param positive-int maxSplitPacketPartCount
	 * @phpstan-param \Closure(EncapsulatedPacket) onRecv
	 * @phpstan-param \Closure(AcknowledgePacket)  sendPacket
	 */
	public ReceiveReliabilityLayer(Action<EncapsulatedPacket> onRecv, Action<AcknowledgePacket> sendPacket, int maxSplitPacketPartCount = Int32.MaxValue, int maxConcurrentSplitPackets = Int32.MaxValue){
		this.onRecv = onRecv;
		this.sendPacket = sendPacket;

        windowStart = 0;
		windowEnd = WINDOW_SIZE;

		reliableWindowStart = 0;
		reliableWindowEnd = WINDOW_SIZE;
        
        for (var i = 0; i < PacketReliability.MAX_ORDER_CHANNELS; i++){
            receiveOrderedIndex[i] = 0;
            receiveSequencedHighestIndex[i] = 0;
            receiveOrderedPackets[i] = new Dictionary<int, EncapsulatedPacket?>();
        }
        
		this.maxSplitPacketPartCount = maxSplitPacketPartCount;
		this.maxConcurrentSplitPackets = maxConcurrentSplitPackets;
	}

	private void HandleEncapsulatedPacketRoute(EncapsulatedPacket pk){
		onRecv(pk);
	}

    /**
	 * Processes a split part of an encapsulated packet.
	 */
    private EncapsulatedPacket? HandleSplit(EncapsulatedPacket packet) {
        if (packet.SplitPacket == null) {
            return packet;
        }
        var totalParts = packet.SplitPacket.TotalPartCount;
        var partIndex = packet.SplitPacket.PartIndex;
        if (
            totalParts >= maxSplitPacketPartCount || totalParts < 0 ||
            partIndex >= totalParts || partIndex < 0
        ) {
            //logger.debug("Invalid split packet part, too many parts || invalid split index (part index partIndex, part count totalParts)");
            return null;
        }

        var splitId = packet.SplitPacket.Id;
        if (splitPackets.ContainsKey(splitId)) {
            if (splitPackets.Count >= maxConcurrentSplitPackets) {
                //logger.debug("Ignored split packet part because reached concurrent split packet limit of maxConcurrentSplitPackets");
                return null;
            }
            for (var i = 0; i < totalParts; i++) {
                splitPackets[splitId][i] = null;
            }
        }
        else if (splitPackets[splitId].Count != totalParts) {
            //logger.debug("Wrong split count totalParts for split packet splitId, expected " . count(splitPackets[splitId]));
            return null;
        }

        splitPackets[splitId][partIndex] = packet;

        var parts = new EncapsulatedPacket?[splitPackets[splitId].Count];
        foreach (var keyValuePair in splitPackets[splitId]) {
            if (keyValuePair.Value == null) {
                return null;
            }
            parts[keyValuePair.Key] = keyValuePair.Value;
        }

        //got all parts, reassemble the packet
        var pk = new EncapsulatedPacket {
            Buffer = new byte[parts.Sum(p => p != null ? p.Buffer.Length : 0)],
            Reliability = packet.Reliability,
            MessageIndex = packet.MessageIndex,
            SequenceIndex = packet.SequenceIndex,
            OrderIndex = packet.OrderIndex,
            OrderChannel = packet.OrderChannel
        };

        for (var i = 0; i < totalParts; ++i) {
            pk.Buffer = pk.Buffer.Union(parts[i]!.Buffer).ToArray();
		}
        
		splitPackets.Remove(splitId);

		return pk;
	}

	private void HandleEncapsulatedPacket(EncapsulatedPacket? packet){
        if (packet == null) {
            return;
        }
		if(packet.MessageIndex != null){
            
			//check for duplicates || out of range
			if(packet.MessageIndex < reliableWindowStart || packet.MessageIndex > reliableWindowEnd || reliableWindow.Count <= packet.MessageIndex){
				return;
			}

			reliableWindow[(int) packet.MessageIndex] = true;

            if(packet.MessageIndex == reliableWindowStart){
                for(; reliableWindow.ContainsKey(reliableWindowStart); ++reliableWindowStart) {
                    reliableWindow.Remove(reliableWindowStart);
                    ++reliableWindowEnd;
                }
            }
		}

		if((packet = HandleSplit(packet)) == null){
			return;
		}

		if(PacketReliability.IsSequencedOrOrdered(packet.Reliability) && (packet.OrderChannel < 0 || packet.OrderChannel >= PacketReliability.MAX_ORDER_CHANNELS)){
			//TODO: this should result in peer banning
			//logger.debug("Invalid packet, bad order channel (packet.orderChannel)");
			return;
		}

		if(PacketReliability.IsSequenced(packet.Reliability)){
			if(packet.SequenceIndex < receiveSequencedHighestIndex[packet.OrderChannel] || packet.OrderIndex < receiveOrderedIndex[packet.OrderChannel]){
				//too old sequenced packet, discard it
				return;
			}

			receiveSequencedHighestIndex[packet.OrderChannel] = packet.SequenceIndex + 1;
			HandleEncapsulatedPacketRoute(packet);
		}else if(PacketReliability.IsOrdered(packet.Reliability)){
			if(packet.OrderIndex == receiveOrderedIndex[packet.OrderChannel]){
				//this is the packet we expected to get next
				//Any ordered packet resets the sequence index to zero, so that sequenced packets older than this ordered
				//one get discarded. Sequenced packets also include (but don't increment) the order index, so a sequenced
				//packet with an order index less than this will get discarded
				receiveSequencedHighestIndex[packet.OrderChannel] = 0;
				receiveOrderedIndex[packet.OrderChannel] = packet.OrderIndex + 1;

				HandleEncapsulatedPacketRoute(packet);
				var i = receiveOrderedIndex[packet.OrderChannel];
                for(; receiveOrderedPackets.ContainsKey(packet.OrderChannel) && receiveOrderedPackets[packet.OrderChannel].ContainsKey(i); ++i){
                    HandleEncapsulatedPacketRoute(receiveOrderedPackets[packet.OrderChannel][i]!);
                    receiveOrderedPackets[packet.OrderChannel].Remove(i);
                }

                receiveOrderedIndex[packet.OrderChannel] = i;
			}else if(packet.OrderIndex > receiveOrderedIndex[packet.OrderChannel]){
				receiveOrderedPackets[packet.OrderChannel][packet.OrderIndex] = packet;
			}else{
				//duplicate/already received packet
			}
		}else{
			//not ordered || sequenced
			HandleEncapsulatedPacketRoute(packet);
		}
	}

	public void OnDatagram(Datagram packet){
		if(packet.SequenceNumber < windowStart || packet.SequenceNumber > windowEnd || ackQueue.Count > packet.SequenceNumber){
			//logger.debug("Received duplicate || out-of-window packet (sequence number packet.seqNumber, window " . windowStart . "-" . windowEnd . ")");
			return;
		}

		nackQueue.Remove(packet.SequenceNumber);
		ackQueue[packet.SequenceNumber] = packet.SequenceNumber;
		if(highestSeqNumber < packet.SequenceNumber){
			highestSeqNumber = packet.SequenceNumber;
		}

		if(packet.SequenceNumber == windowStart){
			//got a contiguous packet, shift the receive window
			//this packet might complete a sequence of out-of-order packets, so we incrementally check the indexes
			//to see how far to shift the window, && stop as soon as we either find a gap || have an empty window
			for(; ackQueue.ContainsKey(windowStart); ++windowStart){
				++windowEnd;
			}
		}else if(packet.SequenceNumber > windowStart){
			//we got a gap - a later packet arrived before earlier ones did
			//we add the earlier ones to the NACK queue
			//if the missing packets arrive before the end of tick, they'll be removed from the NACK queue
			for(var i = windowStart; i < packet.SequenceNumber; ++i){
				if(ackQueue.ContainsKey(i)){
					nackQueue[i] = i;
				}
			}
		}else{
			//assert(false, "received packet before window start");
		}

		foreach (var encapsulatedPacket in packet.Packets) {
            HandleEncapsulatedPacket(encapsulatedPacket);
        }
	}

	public void Update(){
		var diff = highestSeqNumber - windowStart + 1;
		if(diff > 0){
			//Move the receive window to account for packets we either received || are about to NACK
			//we ignore any sequence numbers that we sent NACKs for, because we expect the client to resend them
			//when it gets a NACK for it

			windowStart += diff;
			windowEnd += diff;
		}

		if(ackQueue.Count > 0){
			var pk = new Ack {
                Packets = ackQueue.Values.ToArray()
            };
            sendPacket(pk);
			ackQueue.Clear();
		}

		if(nackQueue.Count > 0){
			var pk = new Nack {
                Packets = nackQueue.Values.ToArray()
            };
            sendPacket(pk);
			nackQueue.Clear();
		}
	}

	public bool NeedsUpdate(){
		return ackQueue.Count != 0 || nackQueue.Count != 0;
	}
}

public sealed class SendReliabilityLayer {
    
	private readonly Action<Datagram> sendDatagramCallback;
    private readonly Action<int> onAck;
    
	private readonly int mtuSize;

    private readonly Dictionary<int, EncapsulatedPacket> sendQueue = new();

	private short splitId;

	private int sendSeqNumber;

	private int messageIndex;

	private readonly Dictionary<int, int> sendOrderedIndex = new();
	private readonly Dictionary<int, int> sendSequencedIndex = new();

    private readonly Dictionary<int, CacheEntry> resendQueue = new();

    private readonly Dictionary<int, CacheEntry> reliableCache = new();

	private readonly Dictionary<int, Dictionary<int, int>> needAck = new();
    
	public SendReliabilityLayer(int mtuSize, Action<Datagram> sendDatagram, Action<int> onAck){
		this.mtuSize = mtuSize;
		sendDatagramCallback = sendDatagram;
		this.onAck = onAck;

        for (var i = 0; i < PacketReliability.MAX_ORDER_CHANNELS; i++) {
            sendOrderedIndex[i] = 0;
            sendSequencedIndex[i] = 0;
        }
    }

	/**
	 * @param EncapsulatedPacket[] packets
	 */
	private void SendDatagram(List<EncapsulatedPacket> packets){
		var datagram = new Datagram();
		datagram.SequenceNumber = sendSeqNumber++;
		datagram.Packets = packets;
		sendDatagramCallback(datagram);

        var resendable = new List<EncapsulatedPacket>();
		foreach(var pk in datagram.Packets){
			if(PacketReliability.IsReliable(pk.Reliability)){
				resendable.Add(pk);
			}
		}
		if(resendable.Count != 0){
			reliableCache[datagram.SequenceNumber] = new CacheEntry(resendable.ToArray());
		}
	}

	public void SendQueue() {
        if (sendQueue.Count <= 0) return;
        SendDatagram(sendQueue.Values.ToList());
        sendQueue.Clear();
    }

	private void AddToQueue(EncapsulatedPacket pk, bool immediate) {
		if(pk.IdentifierAck != null && pk.MessageIndex != null){
			needAck[(int) pk.IdentifierAck][(int) pk.MessageIndex] = (int) pk.MessageIndex;
		}

		var length = (int) Datagram.HEADER_SIZE;
		foreach (var keyValuePair in sendQueue) {
			length += keyValuePair.Value.GetTotalLength();
		}

		if(length + pk.GetTotalLength() > mtuSize - 36){ //IP header (20 bytes) + UDP header (8 bytes) + RakNet weird (8 bytes) = 36 bytes
			SendQueue();
		}

		if(pk.IdentifierAck != null){
			sendQueue.Add(sendQueue.Count, pk.Clone());
			pk.IdentifierAck = null;
		}else{
			sendQueue.Add(sendQueue.Count, pk);
		}

		if(immediate){
			// Forces pending sends to go out now, rather than waiting to the next update interval
			SendQueue();
		}
	}

	public void AddEncapsulatedToQueue(EncapsulatedPacket packet, bool immediate = false) {
		if(packet.IdentifierAck != null) {
            needAck[(int) packet.IdentifierAck] = new Dictionary<int, int>();
        }

		if(PacketReliability.IsOrdered(packet.Reliability)){
			packet.OrderIndex = sendOrderedIndex[packet.OrderChannel]++;
		}else if(PacketReliability.IsSequenced(packet.Reliability)){
			packet.OrderIndex = sendOrderedIndex[packet.OrderChannel]; //sequenced packets don't increment the ordered channel index
			packet.SequenceIndex = sendSequencedIndex[packet.OrderChannel]++;
		}

		//IP header size (20 bytes) + UDP header size (8 bytes) + RakNet weird (8 bytes) + datagram header size (4 bytes) + max encapsulated packet header size (20 bytes)
		var maxSize = mtuSize - 60;

		if(packet.Buffer.Length > maxSize){
            var buffers = new List<byte[]>();
            for (var i = 0; i < packet.Buffer.Length / maxSize; i += 3) {
                buffers.Add(new []{packet.Buffer[i],packet.Buffer[i+1],packet.Buffer[i+2]});
            }
            var bufferCount = buffers.Count;

			splitId = (short) (++splitId % 65536);
            for (var i = 0; i < bufferCount; i++) {
                var pk = new EncapsulatedPacket {
                    SplitPacket = new SplitPacketInfo(splitId, i, bufferCount),
                    Reliability = packet.Reliability,
                    Buffer = buffers[i]
                };

                if(PacketReliability.IsReliable(pk.Reliability)){
                    pk.MessageIndex = messageIndex++;
                }

                pk.SequenceIndex = packet.SequenceIndex;
                pk.OrderChannel = packet.OrderChannel;
                pk.OrderIndex = packet.OrderIndex;

                AddToQueue(pk, true);
            }
        }else{
			if(PacketReliability.IsReliable(packet.Reliability)){
				packet.MessageIndex = messageIndex++;
			}
			AddToQueue(packet, immediate);
		}
	}

	public void OnACK(Ack packet){
		foreach(var sec in packet.Packets) {
            if (!reliableCache.ContainsKey(sec))
                continue;
            foreach(var (_, value) in reliableCache[sec].Packets) {
                if (value.IdentifierAck == null || value.MessageIndex == null) continue;
                needAck[(int) value.IdentifierAck].Remove((int) value.MessageIndex);
                
                if (needAck[(int) value.IdentifierAck].Count != 0) continue;
                needAck.Remove((int) value.IdentifierAck);
                onAck((int) value.IdentifierAck);
            }
            reliableCache.Remove(sec);
        }
	}

	public void OnNACK(Nack packet) {
		foreach(var sec in packet.Packets) {
            if (!reliableCache.ContainsKey(sec)) continue;
            //TODO: group resends if the resulting datagram is below the MTU
            resendQueue.Add(resendQueue.Count, reliableCache[sec]);
            reliableCache.Remove(sec);
        }
	}

	public bool NeedsUpdate() {
		return (
			sendQueue.Count != 0 ||
			resendQueue.Count != 0 ||
			reliableCache.Count != 0
		);
	}

	public void Update() {
		if(resendQueue.Count > 0){
			var limit = 16;
			foreach(var (key, value) in resendQueue){
				SendDatagram(value.Packets.Values.ToList());
                resendQueue.Remove(key);

				if(--limit <= 0){
					break;
				}
			}

			if(resendQueue.Count > ReceiveReliabilityLayer.WINDOW_SIZE){
				resendQueue.Clear();
			}
		}

		foreach(var (key, value) in reliableCache){
			if(value.Timestamp < new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds() - 8000){
				resendQueue.Add(resendQueue.Count, value);
				reliableCache.Remove(key);
			}else{
				break;
			}
		}

		SendQueue();
	}
    
}

public sealed class CacheEntry {

    public Dictionary<int, EncapsulatedPacket> Packets { get; } = new();
    public long Timestamp { get; }
    
    public CacheEntry(EncapsulatedPacket[] packets){
        for (var i = 0; i < packets.Length; ++i){
            Packets[i] = packets[i];
        }
        Timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
    }
}