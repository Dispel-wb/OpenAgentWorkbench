let sessionWriteQueue = Promise.resolve();
let messageWriteQueue = Promise.resolve();
let terminalView = null;
let terminalFitAddon = null;
let terminalSearchAddon = null;
let terminalInputQueue = Promise.resolve();
const markdownCache = new WeakMap();
const dialogReturnFocus = new Map();
