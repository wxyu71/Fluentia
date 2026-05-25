interface EncodeChunkRequest {
  id: number;
  bytes: ArrayBuffer;
}

interface EncodeChunkResponse {
  id: number;
  base64: string;
}

self.onmessage = (event: MessageEvent<EncodeChunkRequest>) => {
  const { id, bytes } = event.data;
  const view = new Uint8Array(bytes);
  let binary = '';
  const segmentSize = 0x8000;

  for (let offset = 0; offset < view.length; offset += segmentSize) {
    binary += String.fromCharCode(...view.subarray(offset, Math.min(offset + segmentSize, view.length)));
  }

  const response: EncodeChunkResponse = {
    id,
    base64: btoa(binary),
  };

  self.postMessage(response);
};
