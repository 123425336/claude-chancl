package com.claudecl.bridge;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import net.minecraftforge.fml.loading.FMLPaths;

import java.io.BufferedReader;
import java.io.BufferedWriter;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.io.Writer;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.HexFormat;

/**
 * 本机桥接服务：只监听回环地址的随机端口，每行一个 JSON 请求、每行一个 JSON 回复。
 * 端口和口令写在当前用户的启动器数据目录里，只有本机同一用户的程序能读到
 */
final class BridgeServer {
    private static String token = "";
    private static Path infoFile;

    private BridgeServer() {
    }

    static void start() {
        try {
            byte[] raw = new byte[24];
            new SecureRandom().nextBytes(raw);
            token = HexFormat.of().formatHex(raw);
            // 明确绑定 IPv4 回环地址：游戏参数可能让默认回环地址变成 IPv6 的 ::1
            ServerSocket server = new ServerSocket(0, 8, InetAddress.getByAddress(new byte[]{127, 0, 0, 1}));
            writeInfo(server.getLocalPort());

            Thread accept = new Thread(() -> acceptLoop(server), "ClaudeCL-Bridge");
            accept.setDaemon(true);
            accept.start();
            // 游戏退出时删除连接信息，避免启动器连到已经不存在的端口
            Runtime.getRuntime().addShutdownHook(new Thread(() -> {
                try {
                    if (infoFile != null) Files.deleteIfExists(infoFile);
                } catch (IOException ignored) {
                }
            }, "ClaudeCL-Bridge-Cleanup"));
            BridgeMod.LOG.info("ClaudeCL 桥接已启动，本机端口 {}", server.getLocalPort());
        } catch (Exception e) {
            BridgeMod.LOG.error("ClaudeCL 桥接启动失败", e);
        }
    }

    /** 连接信息目录：%APPDATA%\ClaudeCL\bridge，文件名是游戏进程号 */
    static Path infoDir() {
        String appData = System.getenv("APPDATA");
        Path base = appData != null && !appData.isEmpty() ? Paths.get(appData) : Paths.get(System.getProperty("user.home"));
        return base.resolve("ClaudeCL").resolve("bridge");
    }

    private static void writeInfo(int port) throws IOException {
        Path dir = infoDir();
        Files.createDirectories(dir);
        long pid = ProcessHandle.current().pid();
        JsonObject o = new JsonObject();
        o.addProperty("pid", pid);
        o.addProperty("host", "127.0.0.1");
        o.addProperty("port", port);
        o.addProperty("token", token);
        o.addProperty("gameDir", FMLPaths.GAMEDIR.get().toAbsolutePath().toString());
        o.addProperty("mcVersion", "1.20.1");
        o.addProperty("loader", "forge");
        o.addProperty("bridgeVersion", BridgeMod.VERSION);
        o.addProperty("startedAt", System.currentTimeMillis());
        infoFile = dir.resolve(pid + ".json");
        // 先写临时文件再改名，启动器不会读到写了一半的内容
        Path tmp = dir.resolve(pid + ".json.tmp");
        Files.writeString(tmp, o.toString(), StandardCharsets.UTF_8);
        Files.move(tmp, infoFile, java.nio.file.StandardCopyOption.REPLACE_EXISTING);
    }

    private static void acceptLoop(ServerSocket server) {
        while (true) {
            try {
                Socket s = server.accept();
                Thread t = new Thread(() -> serve(s), "ClaudeCL-Bridge-Conn");
                t.setDaemon(true);
                t.start();
            } catch (IOException e) {
                BridgeMod.LOG.warn("ClaudeCL 桥接接受连接失败：{}", e.getMessage());
            }
        }
    }

    private static void serve(Socket socket) {
        try (socket;
             BufferedReader in = new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8));
             Writer out = new BufferedWriter(new OutputStreamWriter(socket.getOutputStream(), StandardCharsets.UTF_8))) {
            socket.setTcpNoDelay(true);
            String line;
            while ((line = in.readLine()) != null) {
                if (line.isBlank()) continue;
                boolean[] authed = {false};
                JsonObject resp = handle(line, authed);
                out.write(resp.toString());
                out.write('\n');
                out.flush();
                // 口令不对就断开，不给继续尝试的机会
                if (!authed[0]) return;
            }
        } catch (IOException ignored) {
        }
    }

    private static JsonObject handle(String line, boolean[] authed) {
        JsonObject resp = new JsonObject();
        try {
            JsonObject req = JsonParser.parseString(line).getAsJsonObject();
            if (req.has("id")) resp.add("id", req.get("id"));
            String given = J.str(req, "token", "");
            if (!MessageDigest.isEqual(given.getBytes(StandardCharsets.UTF_8), token.getBytes(StandardCharsets.UTF_8)))
                throw new BridgeException("口令不正确");
            authed[0] = true;
            String op = J.str(req, "op", "");
            JsonObject args = req.has("args") && req.get("args").isJsonObject() ? req.getAsJsonObject("args") : new JsonObject();
            resp.add("result", Ops.run(op, args));
            resp.addProperty("ok", true);
        } catch (BridgeException e) {
            resp.addProperty("ok", false);
            resp.addProperty("error", e.getMessage());
        } catch (Throwable e) {
            BridgeMod.LOG.warn("ClaudeCL 桥接处理请求出错", e);
            resp.addProperty("ok", false);
            resp.addProperty("error", e.getClass().getSimpleName() + ": " + e.getMessage());
        }
        return resp;
    }
}
