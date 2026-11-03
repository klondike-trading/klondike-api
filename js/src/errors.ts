/**
 * An error answer of the API (body `{"code": "...", "error": "..."}`), or a
 * refused WebSocket handshake.
 */
export class KlondikeApiError extends Error {
  /** HTTP status. */
  readonly status: number;
  /** bad_request, unauthorized, not_found, rate_limited, unavailable... */
  readonly code: string;
  /** Seconds to wait before retrying, set on 429 when the server says. */
  readonly retryAfter?: number;

  constructor(status: number, code: string, message: string, retryAfter?: number) {
    super(message);
    this.name = "KlondikeApiError";
    this.status = status;
    this.code = code;
    if (retryAfter !== undefined) this.retryAfter = retryAfter;
  }
}

/** A WebSocket stream ended on an error other than an API error. */
export class KlondikeStreamError extends Error {
  /** WebSocket close code, when the server closed the connection. */
  readonly closeCode?: number;

  constructor(message: string, options?: { closeCode?: number; cause?: unknown }) {
    super(message);
    this.name = "KlondikeStreamError";
    if (options?.closeCode !== undefined) this.closeCode = options.closeCode;
    if (options?.cause !== undefined) (this as { cause?: unknown }).cause = options.cause;
  }
}

const STATUS_TEXT: Record<number, string> = {
  400: "Bad Request",
  401: "Unauthorized",
  403: "Forbidden",
  404: "Not Found",
  429: "Too Many Requests",
  500: "Internal Server Error",
  502: "Bad Gateway",
  503: "Service Unavailable",
  504: "Gateway Timeout",
};

/**
 * Builds the error of a non 200 answer: code and message from the JSON body
 * when it has them, else the status text and the body.
 */
export function apiErrorFrom(status: number, statusText: string, body: string, retryAfterHeader: string | null): KlondikeApiError {
  let code = statusText || STATUS_TEXT[status] || `HTTP ${status}`;
  let message = body.trim();
  try {
    const payload = JSON.parse(body);
    if (payload && typeof payload.error === "string" && payload.error !== "") {
      code = typeof payload.code === "string" ? payload.code : code;
      message = payload.error;
    }
  } catch {
    // not JSON: keep the body
  }
  let retryAfter: number | undefined;
  if (retryAfterHeader !== null && /^\s*\d+\s*$/.test(retryAfterHeader)) {
    retryAfter = parseInt(retryAfterHeader, 10);
  }
  return new KlondikeApiError(status, code, message || code, retryAfter);
}
