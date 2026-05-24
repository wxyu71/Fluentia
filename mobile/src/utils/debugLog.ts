const logs: string[] = [];
const MAX = 200;

export function dlog(tag: string, msg: string) {
  const line = `${new Date().toLocaleTimeString('en-GB', { hour12: false, fractionalSecondDigits: 3 } as any)} [${tag}] ${msg}`;
  logs.push(line);
  if (logs.length > MAX) logs.shift();
  console.log(line);
}

export function getLogs(): string {
  return logs.join('\n');
}

export function clearLogs() {
  logs.length = 0;
}
