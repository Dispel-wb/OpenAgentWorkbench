const fs = require('fs');
const path = require('path');
const root = path.resolve(__dirname, '..');
const source = fs.readFileSync(path.join(root, 'native', 'WorkspaceLayout.cs'), 'utf8');
const program = fs.readFileSync(path.join(root, 'native', 'Program.cs'), 'utf8');
const api = fs.readFileSync(path.join(root, 'native', 'WorkbenchApi.cs'), 'utf8');
for (const required of ['AgentCli', 'AgentShared', 'HarnessRoot', 'sharedLibrary', 'mcpFile']) {
  if (!(source + program).includes(required)) throw new Error(`workspace layout missing ${required}`);
}
if (!source.includes('Path.Combine(CliRoot, id)')) throw new Error('CLI roots are not contained by AgentCli');
if (!program.includes('CliWorkspaces = Path.Combine(siblingRoot, "AgentCli")') || !program.includes('SharedLibrary = Path.Combine(siblingRoot, "AgentShared")')) throw new Error('workspace siblings are not explicit');
if (!api.includes('/api/workbench/tasks/workspace') || !api.includes('CreateTaskWorkspace') || !api.includes('WorkspaceLayout.HarnessRoot(harness)')) throw new Error('per-kernel task workspace endpoint is missing');
console.log(JSON.stringify({ workspaceLayout: 'PASS', siblingRoots: true, cliRootsContained: true, sharedMcpExposed: true }));
