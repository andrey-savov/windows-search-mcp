using WsSearchMcp.Admin;
using WsSearchMcp.Security;

return AdminCli.Run(args, Console.Out, Console.Error, new WindowsCertClientStore(), new ServerPaths(), TimeProvider.System);
