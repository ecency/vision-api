import { parseSupportSettingsPayload, signupClientIp } from "./private-api";

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

describe("parseSupportSettingsPayload", () => {
    it("accepts integers within 0..100", () => {
        expect(parseSupportSettingsPayload({ beneficiary_percent: 5, curation_percent: 10 })).toEqual({
            beneficiary_percent: 5,
            curation_percent: 10
        });
    });

    it("accepts the 0 and 100 boundaries", () => {
        expect(parseSupportSettingsPayload({ beneficiary_percent: 0, curation_percent: 100 })).toEqual({
            beneficiary_percent: 0,
            curation_percent: 100
        });
    });

    it("rejects values out of range", () => {
        expect(parseSupportSettingsPayload({ beneficiary_percent: 101, curation_percent: 10 })).toBeNull();
        expect(parseSupportSettingsPayload({ beneficiary_percent: 5, curation_percent: -1 })).toBeNull();
    });

    it("rejects floats", () => {
        expect(parseSupportSettingsPayload({ beneficiary_percent: 5.5, curation_percent: 10 })).toBeNull();
        expect(parseSupportSettingsPayload({ beneficiary_percent: 5, curation_percent: 0.1 })).toBeNull();
    });

    it("rejects strings", () => {
        expect(parseSupportSettingsPayload({ beneficiary_percent: "5", curation_percent: 10 })).toBeNull();
        expect(parseSupportSettingsPayload({ beneficiary_percent: 5, curation_percent: "10" })).toBeNull();
    });

    it("rejects booleans", () => {
        expect(parseSupportSettingsPayload({ beneficiary_percent: true, curation_percent: 10 })).toBeNull();
        expect(parseSupportSettingsPayload({ beneficiary_percent: 5, curation_percent: false })).toBeNull();
    });

    it("rejects missing fields and bodies", () => {
        expect(parseSupportSettingsPayload({})).toBeNull();
        expect(parseSupportSettingsPayload({ beneficiary_percent: 5 })).toBeNull();
        expect(parseSupportSettingsPayload({ curation_percent: 5 })).toBeNull();
        expect(parseSupportSettingsPayload(undefined)).toBeNull();
        expect(parseSupportSettingsPayload(null)).toBeNull();
    });
});
