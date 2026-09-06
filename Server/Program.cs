using Tennis3D.Server;
var server = new GameServer(args.Length>0 ? int.Parse(args[0]) : Tennis3D.Shared.GameConstants.Port);
await server.RunAsync();
