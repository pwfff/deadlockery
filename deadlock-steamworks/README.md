# Updating Protos
- checkout SteamKit submodule
- checkout their Protobufs submodule
- run generate-all.ps1
- apply diff below
- profit??

```diff
diff --git a/SteamKit2/SteamKit2/Steam/Handlers/SteamGameCoordinator/SteamGameCoordinator.cs b/SteamKit2/SteamKit2/Steam/Handlers/SteamGameCoordinator/SteamGameCoordinator.cs
index 5809d0cb..64f40fff 100644
--- a/SteamKit2/SteamKit2/Steam/Handlers/SteamGameCoordinator/SteamGameCoordinator.cs
+++ b/SteamKit2/SteamKit2/Steam/Handlers/SteamGameCoordinator/SteamGameCoordinator.cs
@@ -37,7 +37,7 @@ namespace SteamKit2
         /// <param name="packetMsg">The packet message that contains the data.</param>
         public override void HandleMsg( IPacketMsg packetMsg )
         {
-            if ( packetMsg.MsgType == EMsg.ClientToGC )
+            if ( packetMsg.MsgType == EMsg.ClientFromGC )
             {
                 var callback = new MessageCallback( packetMsg );
                 this.Client.PostCallback( callback );
```
