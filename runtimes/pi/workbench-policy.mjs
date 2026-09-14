import fs from "node:fs";
import path from "node:path";

const FILE_TOOLS = new Set(["read", "edit", "write", "grep", "find", "ls"]);
const WRITE_TOOLS = new Set(["edit", "write"]);
const PROTECTED_SEGMENTS = new Set([".git", ".claude-gui-v2"]);

function inside(root, candidate) {
	const relative = path.relative(root, candidate);
	return relative === "" || (!relative.startsWith(`..${path.sep}`) && relative !== ".." && !path.isAbsolute(relative));
}

function canonicalCandidate(candidate) {
	let existing = candidate;
	const suffix = [];
	while (!fs.existsSync(existing)) {
		const parent = path.dirname(existing);
		if (parent === existing) break;
		suffix.unshift(path.basename(existing));
		existing = parent;
	}
	return path.resolve(fs.realpathSync.native(existing), ...suffix);
}

export default function workbenchPolicy(pi) {
	const workspace = fs.realpathSync.native(process.env.WORKBENCH_PI_WORKSPACE || process.cwd());
	pi.on("tool_call", async (event) => {
		const tool = String(event.toolName || "").toLowerCase();
		if (!FILE_TOOLS.has(tool)) return undefined;
		const raw = String(event.input?.path || event.input?.file_path || ".");
		let candidate;
		try {
			candidate = canonicalCandidate(path.resolve(workspace, raw.replace(/^@/, "")));
		} catch (error) {
			return { block: true, reason: `Path validation failed: ${error instanceof Error ? error.message : error}` };
		}
		if (!inside(workspace, candidate)) {
			return { block: true, reason: "The Workbench Pi policy limits file tools to the selected workspace." };
		}
		if (WRITE_TOOLS.has(tool)) {
			const segments = path.relative(workspace, candidate).split(path.sep).filter(Boolean);
			if (segments.some((segment) => PROTECTED_SEGMENTS.has(segment.toLowerCase()))) {
				return { block: true, reason: "Workbench metadata and Git internals are protected from Pi writes." };
			}
		}
		return undefined;
	});
}
