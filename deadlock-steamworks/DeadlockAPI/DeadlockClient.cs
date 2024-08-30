using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.GC;
using SteamKit2.GC.Deadlock.Internal;
using SteamKit2.Internal;

namespace DeadlockAPI {
    public class DeadlockClient {
        SteamClient client;

        SteamUser user;
        SteamGameCoordinator gameCoordinator;

        CallbackManager callbackMgr;

        string? lastUsername;
        string? lastToken;
        bool disconnecting = false;

        const int APPID = 1422450;

        uint clientVersion = 0;

        public DeadlockClient() {
            DebugLog.AddListener(new SimpleConsoleDebugListener());
            DebugLog.Enabled = true;

            client = new SteamClient();
            client.DebugNetworkListener = new NetHookNetworkListener();

            user = client.GetHandler<SteamUser>()!;
            gameCoordinator = client.GetHandler<SteamGameCoordinator>()!;

            callbackMgr = new CallbackManager(client);
            callbackMgr.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            callbackMgr.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            callbackMgr.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            callbackMgr.Subscribe<SteamGameCoordinator.MessageCallback>(OnGCMessage);

            if (File.Exists(".username")) {
                 this.lastUsername = File.ReadAllText(".username");
            }

            if (File.Exists(".token")) {
                 this.lastToken = File.ReadAllText(".token");
            }
        }

        public bool IsConnected { get => client.IsConnected; }

        public void Connect() {
            Console.WriteLine("Connecting to Steam");
            disconnecting = false;
            client.Connect();
        }

        public void Disconnect() {
            disconnecting = true;
            client.Disconnect();
        }

        public void Wait() {
            while (true) {
                callbackMgr.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
        }

        public void RunCallbacks(TimeSpan t) {
            callbackMgr.RunWaitCallbacks(t);
        }

        async void OnConnected(SteamClient.ConnectedCallback callback) {
            Console.WriteLine("Connected to Steam");

            if (lastToken == null || lastUsername == null) {
                var authSession = await client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails());

                authSession.ChallengeURLChanged = () =>
                {
                    Console.WriteLine("Steam has refreshed the challenge url");

                    DrawQRCode(authSession);
                };
                
                DrawQRCode(authSession);

                var pollResponse = await authSession.PollingWaitForResultAsync();
                lastUsername = pollResponse.AccountName;
                lastToken = pollResponse.RefreshToken;

                File.WriteAllText(".username", lastUsername);
                File.WriteAllText(".token", lastToken);
            }
            
            Console.WriteLine($"Logging in as '{lastUsername}'...");
            
            user?.LogOn(new SteamUser.LogOnDetails
            {
                Username = lastUsername,
                AccessToken = lastToken,
            });
        }

        void OnDisconnected(SteamClient.DisconnectedCallback callback) {
            if (!disconnecting) {
                Console.WriteLine("Disconnected :(\nTrying again in 10s");
                Thread.Sleep(10000);
                Connect();
            }
        }

        void OnLoggedOn(SteamUser.LoggedOnCallback callback) {
            if (callback.Result != EResult.OK) {
                Console.WriteLine("Unable to log on to Steam: {0}", callback.Result);
                return;
            }

            Console.WriteLine("Logged in! Launching Deadlock");

            var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
            playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed {
                game_id = new GameID(APPID)
            });

            client.Send(playGame);

            Thread.Sleep(5000);

            var clientHello = new ClientGCMsgProtobuf<CMsgCitadelClientHello>((uint)EGCBaseClientMsg.k_EMsgGCClientHello);
            clientHello.Body.region_mode = ECitadelRegionMode.k_ECitadelRegionMode_ROW;
            gameCoordinator.Send(clientHello, APPID);
        }

        void OnGCMessage(SteamGameCoordinator.MessageCallback callback) {
            var messageMap = new Dictionary<uint, Action<IPacketGCMsg>>
            {
                { (uint) EGCBaseClientMsg.k_EMsgGCClientWelcome, OnClientWelcome },
                { (uint) EGCCitadelClientMessages.k_EMsgGCToClientDevPlaytestStatus, OnDevPlaytestStatus },
            };

            Action<IPacketGCMsg> func;
            if (!messageMap.TryGetValue(callback.EMsg, out func)) {
                return;
            }

            func(callback.Message);
        }

        public async Task<U?> SendAndReceiveWithJob<T, U>(ClientGCMsgProtobuf<T> msg)
            where T : ProtoBuf.IExtensible, new()
            where U : ProtoBuf.IExtensible, new() {
            msg.SourceJobID = client.GetNextJobID();
            gameCoordinator.Send(msg, APPID);
            try {
                var cb = await new AsyncJob<SteamGameCoordinator.MessageCallback>(client, msg.SourceJobID);
                var response = new ClientGCMsgProtobuf<U>(cb.Message);

                return response.Body;
            } catch (Exception e) {
                Console.Write(e.ToString());
                return default;
            }
        }

        public class MatchMetaData {
            public required CMsgClientToGCGetMatchMetaDataResponse Data;
            public required string ReplayURL;
            public required string MetadataURL;
        }

        public async Task<MatchMetaData?> GetMatchMetaData(uint matchId) {
            var msg = new ClientGCMsgProtobuf<CMsgClientToGCGetMatchMetaData>((uint)EGCCitadelClientMessages.k_EMsgClientToGCGetMatchMetaData);
            msg.Body.match_id = matchId;
            var r = await SendAndReceiveWithJob<CMsgClientToGCGetMatchMetaData, CMsgClientToGCGetMatchMetaDataResponse>(msg);
            if (r == null) return null;
            Console.WriteLine($"r {r} r.Result {r.result} r.replay_salt {r.replay_salt}");
            return new MatchMetaData() {
                Data = r,
                ReplayURL = $"http://replay{r.cluster_id}.valve.net/{APPID}/{matchId}_{r.replay_salt}.dem.bz2",
                MetadataURL = $"http://replay{r.cluster_id}.valve.net/{APPID}/{matchId}_{r.metadata_salt}.meta.bz2"
            };
        }

        public async Task<CMsgClientToGCSpectateLobbyResponse?> SpectateLobby(ulong lobbyId) {
            var msg = new ClientGCMsgProtobuf<CMsgClientToGCSpectateLobby>((uint)EGCCitadelClientMessages.k_EMsgClientToGCSpectateLobby);
            msg.Body.lobby_id = lobbyId;
            msg.Body.client_version = clientVersion;
            return await SendAndReceiveWithJob<CMsgClientToGCSpectateLobby, CMsgClientToGCSpectateLobbyResponse>(msg);
        }

        public async Task<CMsgClientToGCGetMatchHistoryResponse?> GetMatchHistory() {
            var msg = new ClientGCMsgProtobuf<CMsgClientToGCGetMatchHistory>((uint)EGCCitadelClientMessages.k_EMsgClientToGCGetMatchHistory);
            msg.Body.account_id = user.SteamID.AccountID;
            return await SendAndReceiveWithJob<CMsgClientToGCGetMatchHistory, CMsgClientToGCGetMatchHistoryResponse>(msg);
        }

        public async Task<CMsgClientToGCGetActiveMatchesResponse?> GetActiveMatches() {
            var msg = new ClientGCMsgProtobuf<CMsgClientToGCGetActiveMatches>((uint)EGCCitadelClientMessages.k_EMsgClientToGCGetActiveMatches);
            msg.SourceJobID = client.GetNextJobID();
            gameCoordinator.Send(msg, APPID);
            var cb = await new AsyncJob<SteamGameCoordinator.MessageCallback>(client, msg.SourceJobID);
            var decompressed = Snappier.Snappy.DecompressToArray(new ReadOnlySpan<byte>(cb.Message.GetData(), 24, cb.Message.GetData().Length - 24));
            return ProtoBuf.Serializer.Deserialize<CMsgClientToGCGetActiveMatchesResponse>(new ReadOnlySpan<byte>(decompressed));
        }

        public class ClientWelcomeEventArgs : EventArgs {
            public required CMsgClientWelcome Data;
        }
        public event EventHandler<ClientWelcomeEventArgs> ClientWelcomeEvent;
        void OnClientWelcome(IPacketGCMsg packetMsg) {
            var msg = new ClientGCMsgProtobuf<CMsgClientWelcome>(packetMsg);
            clientVersion = msg.Body.version;
            ClientWelcomeEvent?.Invoke(this, new ClientWelcomeEventArgs() { Data = msg.Body });
        }

        public class DevPlaytestStatusEventArgs : EventArgs {
            public required CMsgGCToClientDevPlaytestStatus Data;
        }
        public event EventHandler<DevPlaytestStatusEventArgs> DevPlaytestStatusEvent;
        void OnDevPlaytestStatus(IPacketGCMsg packetMsg) {
            var msg = new ClientGCMsgProtobuf<CMsgGCToClientDevPlaytestStatus>(packetMsg);
            DevPlaytestStatusEvent?.Invoke(this, new DevPlaytestStatusEventArgs() { Data = msg.Body });
        }

        void DrawQRCode( QrAuthSession authSession )
        {
            Console.WriteLine( $"Challenge URL: {authSession.ChallengeURL}" );
            Console.WriteLine();

            // Encode the link as a QR code
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode( authSession.ChallengeURL, QRCodeGenerator.ECCLevel.L );
            using var qrCode = new AsciiQRCode( qrCodeData );
            var qrCodeAsAsciiArt = qrCode.GetGraphic( 1, drawQuietZones: false );

            Console.WriteLine( "Use the Steam Mobile App to sign in via QR code:" );
            Console.WriteLine( qrCodeAsAsciiArt );
        }
    }
}
