/**
 * SeedToolBox automation tools.
 *
 * The tools themselves run inside SeedToolBox: this extension reads their list from the manifest SeedToolBox
 * writes (STB_TOOLS) and forwards every call over a local named pipe (STB_PIPE, authenticated with STB_TOKEN).
 * SeedToolBox asks the user before anything that changes the computer.
 *
 * Tools from other extensions (installed plugins) are gated here: the user confirms each one, or allows it
 * for the rest of the conversation.
 */

import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Type } from "typebox";
import { readFileSync } from "node:fs";
import { connect } from "node:net";

type ToolSpec = {
	name: string;
	label: string;
	description: string;
	guidelines?: string[];
	parameters: Record<string, unknown>;
};

type Reply = { text?: string; error?: string; image?: string };

function call(tool: string, args: unknown, callId: string, signal?: AbortSignal): Promise<Reply> {
	return new Promise((resolve, reject) => {
		const socket = connect(process.env.STB_PIPE as string);
		let data = "";
		const abort = () => {
			socket.destroy();
			reject(new Error("已取消"));
		};
		signal?.addEventListener("abort", abort, { once: true });
		socket.setEncoding("utf8");
		socket.on("connect", () => {
			socket.write(JSON.stringify({ token: process.env.STB_TOKEN, tool, args, callId }) + "\n");
		});
		socket.on("data", (chunk) => (data += chunk));
		socket.on("end", () => {
			signal?.removeEventListener("abort", abort);
			try {
				resolve(JSON.parse(data) as Reply);
			} catch {
				reject(new Error("SeedToolBox 没有返回结果"));
			}
		});
		socket.on("error", (error) => {
			signal?.removeEventListener("abort", abort);
			reject(new Error("连不上 SeedToolBox：" + error.message));
		});
	});
}

export default function (pi: ExtensionAPI) {
	const manifest = process.env.STB_TOOLS;
	if (!manifest) return;
	const tools = JSON.parse(readFileSync(manifest, "utf8")) as ToolSpec[];
	const own = new Set(tools.map((t) => t.name));
	const allowed = new Set<string>();

	for (const spec of tools) {
		pi.registerTool({
			name: spec.name,
			label: spec.label,
			description: spec.description,
			promptSnippet: spec.description,
			promptGuidelines: spec.guidelines ?? [],
			parameters: Type.Unsafe(spec.parameters),
			async execute(toolCallId, params, signal) {
				const reply = await call(spec.name, params, toolCallId, signal);
				if (reply.error) throw new Error(reply.error);
				const content: any[] = [{ type: "text", text: reply.text ?? "" }];
				if (reply.image) content.push({ type: "image", data: reply.image, mimeType: "image/png" });
				return { content, details: {} };
			},
		});
	}

	pi.on("tool_call", async (event, ctx) => {
		if (own.has(event.toolName) || allowed.has(event.toolName)) return undefined;
		const input = JSON.stringify(event.input, null, 2);
		const choice = await ctx.ui.select(
			`插件工具「${event.toolName}」想要运行：\n\n${input.length > 1500 ? input.slice(0, 1500) + "…" : input}`,
			["允许", "拒绝", "本次对话都允许"],
		);
		if (choice === "本次对话都允许") allowed.add(event.toolName);
		else if (choice !== "允许") return { block: true, reason: "用户拒绝了这次调用" };
		return undefined;
	});
}
