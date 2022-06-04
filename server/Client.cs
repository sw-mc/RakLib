using SkyWing.RakLib.Generic;
using SkyWing.RakLib.Protocol;

namespace SkyWing.RakLib.Server; 

public class Session {
    
    public const int MAX_SPLIT_PART_COUNT = 128;
    public const int MAX_CONCURRENT_SPLIT_COUNT = 4;

    public const int STATE_CONNECTING = 0;
    public const int STATE_CONNECTED = 1;
    public const int STATE_DISCONNECT_PENDING = 2;
    public const int STATE_DISCONNECT_NOTIFIED = 3;
    public const int STATE_DISCONNECTED = 4;

    public const int MIN_MTU_SIZE = 400;

    private readonly Server server;

    public InternetAddress Address { get; }

    public int State { get; private set; } = STATE_CONNECTING;

    public long Id { get; private set; }

    private float lastUpdate;
    private float disconnectionTime;

    public bool IsTemporal { get; private set; } = true;

    private bool isActive;

    private float lastPingTime = -1;
    private long lastPingMeasure = 1;

    public int InternalId { get; }

    private readonly ReceiveReliabilityLayer recvLayer;

    private readonly SendReliabilityLayer sendLayer;

    public Session(Server server, InternetAddress address, long clientId, int mtuSize, int internalId) {
        if (mtuSize < MIN_MTU_SIZE) {
            throw new ArgumentException("MTU size must be at least " + MIN_MTU_SIZE + ", got mtuSize");
        }
        this.server = server;
        //logger = new \PrefixedLogger(logger, "Session: " . address.toString());
        this.Address = address;
        Id = clientId;

        lastUpdate = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();

        this.InternalId = internalId;

        recvLayer = new ReceiveReliabilityLayer(
            HandleEncapsulatedPacketRoute,
            SendPacket,
            MAX_SPLIT_PART_COUNT,
            MAX_CONCURRENT_SPLIT_COUNT
        );
        sendLayer = new SendReliabilityLayer(
            mtuSize,
            SendPacket,
            delegate(int identifierAck) {
                this.server.ServerEventListener.OnPacketAck(internalId, identifierAck);
            }
        );
    }

    public bool Connected => State != STATE_DISCONNECT_PENDING &&
                             State != STATE_DISCONNECT_NOTIFIED &&
                             State != STATE_DISCONNECTED;

    public bool Disconnected => State == STATE_DISCONNECTED;

    public void Update(long time) {
		if(!isActive && (lastUpdate + 10) < time){
			ForciblyDisconnect("timeout");

			return;
		}

		if(State == STATE_DISCONNECT_PENDING || State == STATE_DISCONNECT_NOTIFIED){
			//by this point we already told the event listener that the session is closing, so we don't need to do it again
			if(!sendLayer.NeedsUpdate() && !recvLayer.NeedsUpdate()){
				if(State == STATE_DISCONNECT_PENDING){
					QueueConnectedPacket(new DisconnectionNotification(), PacketReliability.RELIABLE_ORDERED, 0, true);
					State = STATE_DISCONNECT_NOTIFIED;
					//logger.debug("All pending traffic flushed, sent disconnect notification");
				}else{
					State = STATE_DISCONNECTED;
					//logger.debug("Client cleanly disconnected, marking session for destruction");
					return;
				}
			}else if(disconnectionTime + 10 < time){
				State = STATE_DISCONNECTED;
				//logger.debug("Timeout during graceful disconnect, forcibly closing session");
				return;
			}
		}

		isActive = false;

		recvLayer.Update();
		sendLayer.Update();

        if (!(lastPingTime + 5 < time)) return;
        SendPing();
        lastPingTime = time;
    }

	private void QueueConnectedPacket(Packet packet, int reliability, int orderChannel, bool immediate = false) {
		var outgoing = new PacketSerializer();  //TODO: reuse streams to reduce allocations
		packet.Encode(outgoing);

		var encapsulated = new EncapsulatedPacket {
            Reliability = reliability,
            OrderChannel = orderChannel,
            Buffer = outgoing.GetBuffer()
        };

        sendLayer.AddEncapsulatedToQueue(encapsulated, immediate);
	}

	public void AddEncapsulatedToQueue(EncapsulatedPacket packet, bool immediate) {
		sendLayer.AddEncapsulatedToQueue(packet, immediate);
	}

	private void SendPacket(Packet packet) {
		server.SendPacket(packet, Address);
	}

	private void SendPing(int reliability = PacketReliability.UNRELIABLE) {
		QueueConnectedPacket(ConnectPing.Create(server.RakNetTimeMs), reliability, 0, true);
	}

	private void HandleEncapsulatedPacketRoute(EncapsulatedPacket packet) {
        Id = packet.Buffer[0];
		if(Id < MessageIdentifiers.ID_USER_PACKET_ENUM){ //internal data packet
			if(State == STATE_CONNECTING) {
                switch (Id) {
                    case MessageIdentifiers.ID_CONNECTION_REQUEST: {
                        var dataPacket = new ConnectionRequest();
                        dataPacket.Decode(new PacketSerializer(packet.Buffer));
                        QueueConnectedPacket(ConnectionRequestAccepted.Create(
                            Address,
                            Array.Empty<InternetAddress>(),
                            dataPacket.SendPingTime,
                            server.RakNetTimeMs
                        ), PacketReliability.UNRELIABLE, 0, true);
                        break;
                    }
                    case MessageIdentifiers.ID_NEW_INCOMING_CONNECTION: {
                        var dataPacket = new NewIncomingConnection();
                        dataPacket.Decode(new PacketSerializer(packet.Buffer));

                        if (dataPacket.Address.Port != server.Port && server.PortChecking) return;
                        State = STATE_CONNECTED; //FINALLY!
                        IsTemporal = false;
                        server.OpenSession(this);

                        //handlePong(dataPacket.sendPingTime, dataPacket.sendPongTime); //can't use this due to system-address count issues in MCPE >.<
                        SendPing();
                        break;
                    }
                }
            }else switch (Id) {
                case MessageIdentifiers.ID_DISCONNECTION_NOTIFICATION:
                    OnClientDisconnect();
                    break;
                case MessageIdentifiers.ID_CONNECTED_PING: {
                    var dataPacket = new ConnectPing();
                    dataPacket.Decode(new PacketSerializer(packet.Buffer));
                    QueueConnectedPacket(ConnectPong.Create(
                        dataPacket.SendPingTime,
                        server.RakNetTimeMs
                    ), PacketReliability.UNRELIABLE, 0);
                    break;
                }
                default: {
                    if(Id == MessageIdentifiers.ID_CONNECTED_PING){
                        var dataPacket = new ConnectPong();
                        dataPacket.Decode(new PacketSerializer(packet.Buffer));

                        HandlePong(dataPacket.SendPingTime, dataPacket.SendPingTime);
                    }
                    break;
                }
            }
		}else if(State == STATE_CONNECTED){
			server.ServerEventListener.OnPacketReceive(InternalId, packet.Buffer);
		}else{
			//logger.notice("Received packet before connection: " . bin2hex(packet.buffer));
		}
	}

	/**
	 * @param int sendPongTime TODO: clock differential stuff
	 */
	private void HandlePong(long sendPingTime, long sendPongTime) {
        var currentTime = server.RakNetTimeMs;
		if(currentTime < sendPingTime){
			//logger.debug("Received invalid pong: timestamp is in the future by " . (sendPingTime - currentTime) . " ms");
		}else{
			lastPingMeasure = currentTime - sendPingTime;
			server.ServerEventListener.OnPingMeasure(InternalId, lastPingMeasure);
		}
	}

	public void HandlePacket(Packet packet) {
		isActive = true;
		lastUpdate = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();

		if(packet.GetType() == typeof(Datagram)){ //In reality, ALL of these packets are datagrams.
			recvLayer.OnDatagram((Datagram) packet);
		}else if(packet.GetType() == typeof(Ack)){
			sendLayer.OnACK((Ack) packet);
		}else if(packet.GetType() == typeof(Nack)){
			sendLayer.OnNACK((Nack) packet);
		}
	}

	/**
	 * Initiates a graceful asynchronous disconnect which ensures both parties got all packets.
	 */
	public void InitiateDisconnect(string reason) {
        if (!Connected) return;
        State = STATE_DISCONNECT_PENDING;
        disconnectionTime = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
        server.ServerEventListener.OnClientDisconnect(InternalId, reason);
        //logger.debug("Requesting graceful disconnect because \"reason\"");
    }

	/**
	 * Disconnects the session with immediate effect, regardless of current session state. Usually used in timeout cases.
	 */
	public void ForciblyDisconnect(string reason) {
		State = STATE_DISCONNECTED;
		server.ServerEventListener.OnClientDisconnect(InternalId, reason);
		//logger.debug("Forcibly disconnecting session due to \"reason\"");
	}

    private void OnClientDisconnect() {
        //the client will expect an ACK for this; make sure it gets sent, because after forcible termination
        //there won't be any session ticks to update it
        recvLayer.Update();

        if (Connected) {
            //the client might have disconnected after the server sent a disconnect notification, but before the client
            //received it - in this case, we don't want to notify the event handler twice
            server.ServerEventListener.OnClientDisconnect(InternalId, "client disconnect");
        }
        State = STATE_DISCONNECTED;
        //logger.debug("Terminating session due to client disconnect");
    }

}