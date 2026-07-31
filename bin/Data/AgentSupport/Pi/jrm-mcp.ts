import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { createInterface } from "node:readline";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

type JsonRpcReply = {
  id?: number;
  result?: any;
  error?: { code?: number; message?: string; data?: unknown };
};

class McpClient {
  private readonly child: ChildProcessWithoutNullStreams;
  private readonly pending = new Map<
    number,
    { resolve: (value: any) => void; reject: (reason: Error) => void }
  >();
  private nextId = 1;

  constructor(command: string, args: string[], cwd: string) {
    this.child = spawn(command, args, {
      cwd,
      windowsHide: true,
      stdio: ["pipe", "pipe", "pipe"],
    });

    createInterface({ input: this.child.stdout }).on("line", (line) => {
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

    const rejectAll = (reason: Error) => {
      for (const request of this.pending.values()) request.reject(reason);
      this.pending.clear();
    };
    this.child.on("error", rejectAll);
    this.child.on("exit", (code) =>
      rejectAll(new Error(`JeekRemoteManager MCP adapter exited (${code ?? "unknown"}).`)),
    );
  }

  call(method: string, params?: unknown): Promise<any> {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      const message: Record<string, unknown> = { jsonrpc: "2.0", id, method };
      if (params !== undefined) message.params = params;
      this.child.stdin.write(`${JSON.stringify(message)}\n`, (error) => {
        if (!error) return;
        this.pending.delete(id);
        reject(error);
      });
    });
  }

  notify(method: string, params?: unknown): void {
    const message: Record<string, unknown> = { jsonrpc: "2.0", method };
    if (params !== undefined) message.params = params;
    this.child.stdin.write(`${JSON.stringify(message)}\n`);
  }

  close(): void {
    this.child.kill();
  }
}

async function connectAndRegister(pi: ExtensionAPI, cwd: string): Promise<McpClient | undefined> {
  const configPath = join(cwd, ".mcp.json");
  let config: any;
  try {
    config = JSON.parse(await readFile(configPath, "utf8"));
  } catch {
    return;
  }

  const server = config?.mcpServers?.["jrm-remote"];
  if (!server || typeof server.command !== "string") return;
  const args = Array.isArray(server.args)
    ? server.args.filter((value: unknown): value is string => typeof value === "string")
    : [];

  const client = new McpClient(server.command, args, cwd);
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
        typeof tool?.inputSchema !== "object"
      ) {
        continue;
      }

      const mcpName = tool.name;
      const piName = `jrm_remote__${mcpName.replace(/[^a-zA-Z0-9_-]/g, "_")}`;
      pi.registerTool({
        name: piName,
        label: `JeekRemoteManager: ${mcpName}`,
        description: tool.description,
        promptSnippet: `Run ${mcpName} through the current JeekRemoteManager connection`,
        promptGuidelines: [
          `Use ${piName} for work on the remote server; Pi's local tools operate on this Windows machine.`,
        ],
        parameters: tool.inputSchema,
        async execute(_toolCallId, parameters, signal, _onUpdate, ctx) {
          if (signal?.aborted) throw new Error("Tool call cancelled.");
          if (pi.getFlag("jrm-auto-run") !== true) {
            if (!ctx.hasUI) {
              throw new Error(`Approval required before running ${mcpName}.`);
            }
            const approved = await ctx.ui.confirm(
              "JeekRemoteManager remote tool",
              `Allow ${mcpName}?`,
              { signal },
            );
            if (!approved) throw new Error("Tool call declined.");
          }

          const result = await client.call("tools/call", {
            name: mcpName,
            arguments: parameters,
          });
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
  pi.registerFlag("jrm-auto-run", {
    description: "Allow only JeekRemoteManager MCP tools without Pi confirmation",
    type: "boolean",
    default: false,
  });

  // Pi may load extensions during resource discovery without ever starting a session. Defer the
  // adapter process until session_start, as required for long-lived extension resources.
  let client: McpClient | undefined;
  pi.on("session_start", async (_event, ctx) => {
    if (client) return;
    try {
      client = await connectAndRegister(pi, ctx.cwd);
    } catch (error) {
      if (ctx.hasUI) {
        ctx.ui.notify(
          `JeekRemoteManager MCP: ${error instanceof Error ? error.message : String(error)}`,
          "error",
        );
      }
      throw error;
    }
  });
  pi.on("session_shutdown", () => {
    client?.close();
    client = undefined;
  });
}
