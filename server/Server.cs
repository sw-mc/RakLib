using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using SkyWing.Binary;
using SkyWing.RakLib.Generic;
using SkyWing.RakLib.Protocol;
using SkyWing.RakLib.server;

namespace SkyWing.RakLib.Server;

public interface ServerInterface {
	
	
}

public class Server : ServerInterface {

    private const int RAKLIB_TPS = 100;
    private const int RAKLIB_TIME_PER_TICK = 1 / RAKLIB_TPS;

    protected RakNetSocket Socket { get; }
    private bool SocketError { get; set; } = false;
    public long ServerId { get; }

    protected int ReceivedBytes { get; set; }
    protected int SendBytes { get; set; }

    protected readonly Dictionary<string, int> IpAddressToSessionId = new();
    protected readonly Dictionary<int, Session> Sessions = new();

    protected UnconnectedMessageHandler UnconnectedMessageHandler { get; }
    public string Name { get; set; }

    public int PacketLimit { get; set; } = 200;
    
    protected bool Shutdown { get; set; }

    protected int TickCounter { get; set; }

    protected Dictionary<string, long> Block { get; } = new();
    protected Dictionary<string, int> IpSec { get; } = new();

    protected List<string> PacketFilters { get; } = new();

    public int Port => Socket.BindAddress.Port;

    public bool PortChecking { get; set; } = false;
    
    protected long StartTimeMs { get; }

    public int MaxMtuSize { get; }

    protected int NextSessionId { get; set; }

    public ServerEventListener ServerEventListener { get; }
    public ServerEventSource ServerEventSource { get; }

    public Server(long serverId, RakNetSocket socket, int maxMtuSize, ProtocolAcceptor protocolAcceptor,
        ServerEventSource serverEventSource, ServerEventListener serverEventListener) {
        if (maxMtuSize < Session.MIN_MTU_SIZE) {
            throw new ArgumentException("MaxMtuSize must be at least " + Session.MIN_MTU_SIZE + ", got " + maxMtuSize);
        }

        ServerId = serverId;
        Socket = socket;
        MaxMtuSize = maxMtuSize;
        ServerEventSource = serverEventSource;
        ServerEventListener = serverEventListener;

        StartTimeMs = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
        UnconnectedMessageHandler = new UnconnectedMessageHandler(this, protocolAcceptor);
    }
    
    public long RakNetTimeMs => new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds() - StartTimeMs;

    public void TickProcessor() {

        var start = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
        var stream = !Shutdown;

        do {
            for (var i = 0; i < 100 && stream && !Shutdown; ++i) {
                stream = ServerEventSource.Process(this);
            }

            for (var i = 0; i < 100 && !SocketError; ++i) {
                ReceivePacket();
            }
        } while (stream || !SocketError);
        
        Tick();

        var time = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds() - start;
        if (time < RAKLIB_TIME_PER_TICK) {
            Thread.Sleep((int) (new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds() + RAKLIB_TIME_PER_TICK -
                                time));
        }

    }

    public void WaitShutdown() {
        Shutdown = true;

        while (ServerEventSource.Process(this)) {
            //Ensure that any late messages are processed before we start initiating server disconnects, so that if the
            //server implementation used a custom disconnect mechanism (e.g. a server transfer), we don't break it in
            //race conditions.
        }

        foreach (var keyValuePair in Sessions) {
            //TODO: Start disconnect
        }

        while (Sessions.Count > 0) {
            TickProcessor();
        }
        
        Socket.Close();
        //TODO: Log graceful shutdown
    }

    private void Tick() {
        var time = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
        foreach(var (_, session) in Sessions) {
            session.Update(time);
            if(session.Disconnected){
                RemoveSessionInternal(session);
            }
        }

        IpSec.Clear();

        if(!Shutdown && (TickCounter % RAKLIB_TPS) == 0){
            if(SendBytes > 0 || ReceivedBytes > 0){
                ServerEventListener.OnBandwidthStatsUpdate(SendBytes, ReceivedBytes);
                SendBytes = ReceivedBytes = 0;
            }

            if(Block.Count > 0){
                var keys = Block.Keys.ToList();
                var values = Block.Values.ToArray();
                Array.Sort(values);
                
                var now = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
                var i = 0;
                foreach(var timeout in values){
                    if(timeout <= now) {
                        Block.Remove(keys[i]);
                        ++i;
                    }else{
                        break;
                    }
                }
            }
        }

        ++TickCounter;
    }

    private void ReceivePacket() {
        Socket.ReadPacket(delegate(byte[] bytes, IPAddress a, int port) {
                if (bytes.Length <= 0) {
                    SocketError = true; return;
                }
                var address = new InternetAddress(a.ToString(), port, this.Socket.BindAddress.Version);
                var len = bytes.Length;

                ReceivedBytes += len;
                if (Block.ContainsKey(address.ToString())) {
                    return;
                }

                if (IpSec.ContainsKey(address.ToString())) {
                    if (++IpSec[address.ToString()] >= PacketLimit) {
                        BlockAddress(address.ToString());
                        return;
                    }
                }
                else {
                    IpSec[address.ToString()] = 1;
                }

                if (len < 1) {
                    return;
                }


                try {
                    var session = GetSession(address);
                    if (session != null) {
                        var header = bytes[0];
                        if ((header & Datagram.BITFLAG_VALID) != 0) {
                            Packet packet;
                            if ((header & Datagram.BITFLAG_ACK) != 0) {
                                packet = new Ack();
                            }
                            else if ((header & Datagram.BITFLAG_NAK) != 0) {
                                packet = new Nack();
                            }
                            else {
                                packet = new Datagram();
                            }
                            packet.Decode(new PacketSerializer(bytes));
                            session.HandlePacket(packet);
                            return;
                        }
                        else if (session.Connected) {
                            //allows unconnected packets if the session is stuck in DISCONNECTING state, useful if the client
                            //didn't disconnect properly for some reason (e.g. crash)
                            /*this.logger.debug(
                                "Ignored unconnected packet from address due to session already opened (0x"
                                    .bin2hex(buffer[0]). ")");*/
                            return;
                        }
                    }

                    if (Shutdown) return;
                    var handled = UnconnectedMessageHandler.HandleRaw(bytes, address);
                    if (!handled) {
                        if (PacketFilters.Any(pattern => Regex.IsMatch(Encoding.ASCII.GetString(bytes), pattern))) {
                            handled = true;
                            ServerEventListener.OnRawPacketReceive(address.Ip, address.Port, bytes);
                        }
                    }

                    if (!handled) {
                        /*this.logger.debug(
                                "Ignored packet from address due to no session opened (0x".bin2hex(buffer[0]). ")");*/
                    }
                }
                catch (BinaryDataException e) {
                    /*logFn = function() use(address, e, buffer) : void {
                        this.logger.debug("Packet from address (".length(buffer). " bytes): 0x".bin2hex(buffer));
                        this.logger.debug(
                            get_class(e). ": ".e.getMessage(). " in ".e.getFile(). " on line ".e.getLine());
                        foreach (this.traceCleaner.getTrace(0, e.getTrace()) as line){
                            this.logger.debug(line);
                        }
                        this.logger.error("Bad packet from address: ".e.getMessage());
                    }
                    ;
                    if (this.logger instanceof \BufferedLogger){
                        this.logger.buffer(logFn);
                    }else{
                        logFn();
                    }*/
                    BlockAddress(address.ToString(), 5);
                }
            },
            delegate(SocketException exception) {
                if (exception.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset) {
                }
                else {
                    SocketError = true;
                    //TODO: Log error
                }
            });
    }

    public async void SendPacket(Packet packet, InternetAddress address) {
		var outgoing = new PacketSerializer(); //TODO: reusable streams to reduce allocations
		packet.Encode(outgoing);
		try{
			SendBytes += await Socket.WritePacket(outgoing.GetBuffer(), address.IpAddress, address.Port);
		}catch(SocketException e){
			//this.logger.debug(e.getMessage());
		}
	}

    public void SendEncapsulated(int sessionId, EncapsulatedPacket packet, bool immediate = false) {
		var session = Sessions[sessionId];
		if(session.Connected){
			session.AddEncapsulatedToQueue(packet, immediate);
		}
	}

	public async void SendRaw(string address, int port, byte[] payload) {
		try{
			await Socket.WritePacket(payload, IPAddress.Parse(address), port);
		}catch(SocketException e){
			//this.logger.debug(e.getMessage());
		}
	}

	public void CloseSession(int sessionId) {
		if(Sessions.ContainsKey(sessionId)){
			Sessions[sessionId].InitiateDisconnect("server disconnect");
		}
	}

    private void BlockAddress(string address, int timeout = 300) {
		var final = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() + timeout;
		if(!Block.ContainsKey(address) || timeout == -1){
			if(timeout == -1){
				final = Int64.MaxValue;
			}else{
				//this.logger.notice("Blocked address for timeout seconds");
			}
			Block[address] = final;
		}else if(Block[address] < final){
			Block[address] = final;
		}
	}

	public void UnblockAddress(string address) {
        Block.Remove(address);
        //this.logger.debug("Unblocked address");
    }

	public void AddRawPacketFilter(string regex) {
		PacketFilters.Add(regex);
	}

	public Session? GetSession(InternetAddress address) {
		return IpAddressToSessionId.ContainsKey(address.ToString()) ? Sessions[IpAddressToSessionId[address.ToString()]] : null;
	}
    
    public Session? GetSession(int sessionId) {
        return Sessions.ContainsKey(sessionId) ? Sessions[sessionId] : null;
    }

    public bool SessionExists(InternetAddress address) {
        return IpAddressToSessionId.ContainsKey(address.ToString());
    }
    
    public bool SessionExists(int sessionId) {
        return Sessions.ContainsKey(sessionId);
    }

	public Session CreateSession(InternetAddress address, long clientId, int mtuSize) {
		var existingSession = GetSession(address) ?? null;
		if(existingSession != null){
			existingSession.ForciblyDisconnect("client reconnect");
			this.RemoveSessionInternal(existingSession);
		}

		this.CheckSessions();

		while(Sessions.ContainsKey(NextSessionId)){
			NextSessionId++;
			NextSessionId &= 0x7fffffff; //we don't expect more than 2 billion simultaneous connections, && this fits in 4 bytes
		}

		var session = new Session(this, address.Clone(), clientId, mtuSize, NextSessionId);
		IpAddressToSessionId[address.ToString()] = NextSessionId;
		Sessions[NextSessionId] = session;
		//this.logger.debug("Created session for address with MTU size mtuSize");

		return session;
	}

	private void RemoveSessionInternal(Session session) {
		IpAddressToSessionId.Remove(session.Address.ToString());
        Sessions.Remove(session.InternalId); 
    }

	public void OpenSession(Session session) {
        var address = session.Address;
		ServerEventListener.OnClientConnect(session.InternalId, address.Ip, address.Port, session.Id);
	}

    private void CheckSessions() {
        if (Sessions.Count <= 4096) return;
        foreach (var (_, value) in Sessions) {
            if (!value.IsTemporal) continue;
            RemoveSessionInternal(value);
            if (Sessions.Count <= 4096) {
                break;
            }
        }
    }

    public void NotifyAck(Session session, int identifierAck) {
		this.ServerEventListener.OnPacketAck(session.InternalId, identifierAck);
	}
    
}

public class UnconnectedMessageHandler {

    private readonly Server server;
    private Dictionary<int, Type> packetPool = new();
    private readonly ProtocolAcceptor protocolAcceptor;

    public UnconnectedMessageHandler(Server server, ProtocolAcceptor acceptor) {
        RegisterPackets();
        this.server = server;
        protocolAcceptor = acceptor;
    }

    public bool HandleRaw(byte[] buffer, InternetAddress address) {
        if (buffer.Length < 1) {
            return false;
        }

        var pk = GetPacketFromPool(buffer);
        if (pk == null) {
            return false;
        }

        var reader = new PacketSerializer(buffer);
        pk.Decode(reader);
        if (!pk.IsValid()) {
            return false;
        }
        if (!reader.Feof()) {
            //TODO: Log
        }

        return Handle(pk, address);
    }

    private bool Handle(OfflineMessage packet, InternetAddress address) {
        if(packet.GetType() == typeof(UnconnectedPing)){
			server.SendPacket(UnconnectedPong.Create(((UnconnectedPong) packet).SendPingTime, server.ServerId, server.Name), address);
		}else if(packet.GetType() == typeof(OpenConnectionRequest1)){
			if(!protocolAcceptor.Accepts(((OpenConnectionRequest1)packet).Protocol)){
				server.SendPacket(IncompatibleProtocolVersion.Create(this.protocolAcceptor.GetPrimaryVersion(), server.ServerId), address);
				//this.server.getLogger().notice("Refused connection from address due to incompatible RakNet protocol version (version packet.protocol)");
			}else{
				//IP header size (20 bytes) + UDP header size (8 bytes)
				server.SendPacket(OpenConnectionReply1.Create(server.ServerId, false, (short) (((OpenConnectionRequest2)packet).MtuSize + 28)), address);
			}
		}else if(packet.GetType() == typeof(OpenConnectionRequest2)){
			if(((OpenConnectionRequest2)packet).ServerAddress.Port == server.Port || !server.PortChecking){
				if(((OpenConnectionRequest2)packet).MtuSize < Session.MIN_MTU_SIZE){
					//this.server.getLogger().debug("Not creating session for address due to bad MTU size packet.mtuSize");
					return true;
				}
				var existingSession = server.GetSession(address);
				if(existingSession is {Connected: true}){
					//for redundancy, in case someone rips up Server - we really don't want connected sessions getting
					//overwritten
					//server.getLogger().debug("Not creating session for address due to session already opened");
					return true;
				}
				var mtuSize = (short) Math.Min(((OpenConnectionRequest2)packet).MtuSize, server.MaxMtuSize); //Max size, do not allow creating large buffers to fill server memory
				server.SendPacket(OpenConnectionReply2.Create(this.server.ServerId, address, mtuSize, false), address);
				server.CreateSession(address, ((OpenConnectionRequest2)packet).ClientId, mtuSize);
			}else{
				//this.server.getLogger().debug("Not creating session for address due to mismatched port, expected " . this.server.getPort() . ", got " . packet.serverAddress.getPort());
			}
		}else{
			return false;
		}

		return true;
    }

    private void RegisterPacket(int id, Type type) {
        packetPool.Add(id, type);
    }

    private OfflineMessage? GetPacketFromPool(byte[] buffer) {
        var id = buffer[0]; // Read first byte without moving pointer
        if (packetPool.ContainsKey(id)) {
            return (OfflineMessage?) Activator.CreateInstance(packetPool[id]);
        }

        return null;
    }

    private void RegisterPackets() {
        packetPool = new Dictionary<int, Type>();
        
    }
}

public interface ServerEventListener {
	
	public void OnClientConnect(int sessionId, string address, int port, long clientId);

	public void OnClientDisconnect(int sessionId, string reason);

	public void OnPacketReceive(int sessionId, byte[] packet);

	public void OnRawPacketReceive(string address, int port, byte[] payload);

	public void OnPacketAck(int sessionId, int identifierAck);

	public void OnBandwidthStatsUpdate(int bytesSentDiff, int bytesReceivedDiff);

	public void OnPingMeasure(int sessionId, long pingMs);
	
}

public interface ServerEventSource {

	public bool Process(ServerInterface server);
	
}