import { spawn, type ChildProcess } from "node:child_process";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { createInterface, type Interface as ReadlineInterface } from "node:readline";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

type JsonRpcReply = {
  id?: number;
  result?: any;
  error?: { code?: number; message?: string; data?: unknown };
};

class McpClient {
  private readonly child: ChildProcess;
  private readonly reader: ReadlineInterface;
  private readonly pending = new Map<
    number,
    { resolve: (value: any) => void; reject: (reason: Error) => void }
  >();
  private nextId = 1;

  constructor(command: string, args: string[], cwd: string) {
    this.child = spawn(command, args, {
      cwd,
      windowsHide: true,
      stdio: ["pipe", "pipe", "ignore"],
    });

    const rejectAll = (reason: Error) => {
      for (const request of this.pending.values()) request.reject(reason);
      this.pending.clear();
    };

    this.child.stdin?.on("error", () => {
      // Handled per-request via write callbacks / child exit.
    });

    this.reader = createInterface({ input: this.child.stdout! });
    this.reader.on("line", (line) => {
      let reply: JsonRpcReply;
      try {
        reply = JSON.parse(line);
      } catch {
        return;
      }
      if (typeof reply.id !== "number") return;
      const request = this.pending.get(reply.id);
      if (!request) return;
      this.pending.delete(reply.id);
      if (reply.error) {
        request.reject(new Error(reply.error.message ?? `MCP error ${reply.error.code ?? ""}`));
      } else {
        request.resolve(reply.result);
      }
    });

    this.child.on("error", rejectAll);
    this.child.on("exit", (code) =>
      rejectAll(new Error(`JeekRemoteManager MCP adapter exited (${code ?? "unknown"}).`)),
    );
  }

  call(method: string, params?: unknown, signal?: AbortSignal): Promise<any> {
    if (signal?.aborted) {
      return Promise.reject(new Error("Tool call cancelled."));
    }
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const cleanup = () => {
        signal?.removeEventListener("abort", onAbort);
      };
      const onAbort = () => {
        if (!this.pending.delete(id)) return;
        cleanup();
        this.notify("notifications/cancelled", { requestId: id });
        reject(new Error("Tool call cancelled."));
      };

      this.pending.set(id, {
        resolve: (value) => {
          cleanup();
          resolve(value);
        },
        reject: (reason) => {
          cleanup();
          reject(reason);
        },
      });
      signal?.addEventListener("abort", onAbort, { once: true });

      const message: Record<string, unknown> = { jsonrpc: "2.0", id, method };
      if (params !== undefined) message.params = params;
      if (!this.child.stdin || this.child.stdin.destroyed) {
        this.pending.delete(id);
        cleanup();
        reject(new Error("JeekRemoteManager MCP adapter stdin is closed."));
        return;
      }
      this.child.stdin.write(`${JSON.stringify(message)}\n`, (error) => {
        if (!error) return;
        if (this.pending.delete(id)) {
          cleanup();
          reject(error);
        }
      });
    });
  }

  notify(method: string, params?: unknown): void {
    if (!this.child.stdin || this.child.stdin.destroyed) return;
    const message: Record<string, unknown> = { jsonrpc: "2.0", method };
    if (params !== undefined) message.params = params;
    this.child.stdin.write(`${JSON.stringify(message)}\n`, () => {
      // Ignore write failures on notifications during shutdown.
    });
  }

  close(): void {
    this.reader.close();
    this.child.kill();
  }
}

function findJrmServerEntry(mcpServers: any): [string, any] | undefined {
  if (!mcpServers || typeof mcpServers !== "object") return undefined;
  for (const preferred of ["jrm-remote", "jeek-remote-manager"]) {
    if (mcpServers[preferred]) return [preferred, mcpServers[preferred]];
  }
  for (const [name, entry] of Object.entries(mcpServers)) {
    if (name.startsWith("jrm-remote-")) return [name, entry];
  }
  return undefined;
}

async function connectAndRegister(pi: ExtensionAPI, cwd: string): Promise<McpClient | undefined> {
  const configPath = join(cwd, ".mcp.json");
  let config: any;
  try {
    config = JSON.parse(await readFile(configPath, "utf8"));
  } catch {
    return;
  }

  const found = findJrmServerEntry(config?.mcpServers);
  if (!found) return;
  const [serverName, server] = found;
  if (!server || typeof server.command !== "string") return;
  const args = Array.isArray(server.args)
    ? server.args.filter((value: unknown): value is string => typeof value === "string")
    : [];

  const client = new McpClient(server.command, args, cwd);
  const toolPrefix = serverName.replace(/[^a-zA-Z0-9_-]/g, "_");
  try {
    await client.call("initialize", {
      protocolVersion: "2025-06-18",
      capabilities: {},
      clientInfo: { name: "JeekRemoteManager Pi extension", version: "1" },
    });
    client.notify("notifications/initialized");
    const listed = await client.call("tools/list");

    for (const tool of listed?.tools ?? []) {
      if (
        typeof tool?.name !== "string" ||
        typeof tool?.description !== "string" ||
        !tool?.inputSchema ||
        typeof tool.inputSchema !== "object"
      ) {
        continue;
      }

      const mcpName = tool.name;
      const piName = `${toolPrefix}__${mcpName.replace(/[^a-zA-Z0-9_-]/g, "_")}`;
      pi.registerTool({
        name: piName,
        label: `JeekRemoteManager: ${mcpName}`,
        description: tool.description,
        promptSnippet: `Run ${mcpName} through JeekRemoteManager`,
        promptGuidelines: [
          `Use ${piName} for work on the remote server; Pi's local tools operate on this Windows machine.`,
        ],
        parameters: tool.inputSchema,
        async execute(_toolCallId, parameters, signal, _onUpdate) {
          if (signal?.aborted) throw new Error("Tool call cancelled.");
          const result = await client.call(
            "tools/call",
            {
              name: mcpName,
              arguments: parameters,
            },
            signal,
          );
          const text = (result?.content ?? [])
            .map((item: any) =>
              item?.type === "text" ? item.text : JSON.stringify(item),
            )
            .filter(Boolean)
            .join("\n");
          if (result?.isError) throw new Error(text || `${mcpName} failed.`);
          return {
            content: [{ type: "text", text: text || "(no output)" }],
            details: { mcpTool: mcpName },
          };
        },
      });
    }
  } catch (error) {
    client.close();
    throw error;
  }

  return client;
}

export default function (pi: ExtensionAPI) {
  // Pi may load extensions during resource discovery without ever starting a session. Defer the
  // adapter process until session_start, as required for long-lived extension resources.
  let client: McpClient | undefined;
  let connecting: Promise<McpClient | undefined> | undefined;
  pi.on("session_start", async (_event, ctx) => {
    if (client) return;
    if (connecting) {
      await connecting;
      return;
    }
    const promise = connectAndRegister(pi, ctx.cwd);
    connecting = promise;
    try {
      client = await promise;
    } catch (error) {
      if (ctx.hasUI) {
        ctx.ui.notify(
          `JeekRemoteManager MCP: ${error instanceof Error ? error.message : String(error)}`,
          "error",
        );
      }
      throw error;
    } finally {
      if (connecting === promise) connecting = undefined;
    }
  });
  pi.on("session_shutdown", () => {
    connecting = undefined;
    client?.close();
    client = undefined;
  });
}
