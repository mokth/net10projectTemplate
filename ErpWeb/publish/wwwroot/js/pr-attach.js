export async function uploadPrAttachment(docId, file, antiforgeryToken) {
    const id = encodeURIComponent((docId ?? "").trim());
    const formData = new FormData();
    formData.append("file", file);

    try {
        const response = await fetch(`/purchase/requisitions/attachments/${id}`, {
            method: "POST",
            body: formData,
            credentials: "include",
            headers: antiforgeryToken
                ? { RequestVerificationToken: antiforgeryToken }
                : undefined
        });

        const message = await readMessage(response);
        return {
            ok: response.ok,
            status: response.status,
            message: message ?? (response.ok ? "Attachment uploaded." : "Upload failed.")
        };
    } catch (error) {
        return {
            ok: false,
            status: 0,
            message: error instanceof Error ? error.message : "Upload failed."
        };
    }
}

export function openFilePicker(input) {
    input?.click();
}

export async function uploadPrAttachmentFromInput(input, docId, antiforgeryToken) {
    const file = input?.files?.[0];
    if (!file) {
        return { ok: false, status: 0, message: "Choose a file first." };
    }

    const result = await uploadPrAttachment(docId, file, antiforgeryToken);
    if (input) {
        input.value = "";
    }
    return result;
}

async function readMessage(response) {
    try {
        const contentType = response.headers.get("content-type") ?? "";
        if (contentType.includes("application/json")) {
            const payload = await response.json();
            return payload?.message ?? payload?.title ?? null;
        }

        const text = await response.text();
        return text || null;
    } catch {
        return null;
    }
}
