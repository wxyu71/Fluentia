/// <reference types="vite/client" />

declare module 'tweetnacl-util' {
  export function encodeUTF8(arr: Uint8Array): string;
  export function decodeUTF8(str: string): Uint8Array;
  export function encodeBase64(arr: Uint8Array): string;
  export function decodeBase64(str: string): Uint8Array;
}
