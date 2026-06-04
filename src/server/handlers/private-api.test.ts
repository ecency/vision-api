import { signupClientIp } from "./private-api";

// Minimal express.Request stand-in: signupClientIp only reads `headers`.
const reqWith = (headers: Record<string, string | string[]>): any => ({ headers });

describe("signupClientIp", () => {
    it("uses the proxy-set X-Real-IP", () => {
        expect(signupClientIp(reqWith({ "x-real-ip": "203.0.113.9" }))).toBe("203.0.113.9");
    });

    it("prefers X-Real-IP over X-Forwarded-For", () => {
        expect(
            signupClientIp(reqWith({ "x-forwarded-for": "198.51.100.4", "x-real-ip": "203.0.113.9" }))
        ).toBe("203.0.113.9");
    });

    it("does not fall back to X-Forwarded-For", () => {
        expect(signupClientIp(reqWith({ "x-forwarded-for": "198.51.100.4" }))).toBe("");
    });

    it("returns '' when X-Real-IP is absent", () => {
        expect(signupClientIp(reqWith({}))).toBe("");
    });

    it("takes the first value when X-Real-IP is an array", () => {
        expect(signupClientIp(reqWith({ "x-real-ip": ["198.51.100.7", "10.0.0.1"] }))).toBe(
            "198.51.100.7"
        );
    });
});
