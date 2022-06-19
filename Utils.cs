using System.Net;

namespace SkyWing.RakLib;

// Wrapper class for internet address related data.
public class InternetAddress {

	public string Ip { get; }

	public int Port {
		get => _port;
		private set {
			if (value is < 0 or > 65535) {
				throw new ArgumentOutOfRangeException("Port must be between 0 and 65535");
			}

			_port = value;
		}
	}

	private int _port;

	public InternetVersion Version { get; }
    
    public IPAddress IpAddress => IPAddress.Parse(Ip);
    public IPEndPoint IpEndPoint => new(IpAddress, Port);
    
	public InternetAddress(string ip, int port, InternetVersion version) {
		Ip = ip;
		Port = port;
		Version = version;
	}
    
    public override string ToString() {
        return $"{Ip}:{Port}";
    }
    
    public InternetAddress Clone() => new InternetAddress(Ip, Port, Version);
}

public enum InternetVersion {
	Ipv4 = 4,
	Ipv6 = 6
}