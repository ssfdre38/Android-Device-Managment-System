async function handleLogin(event) {
    event.preventDefault();
    const passwordInput = document.getElementById("password");
    const errorDiv = document.getElementById("error-message");
    
    errorDiv.style.display = "none";
    
    try {
        const response = await fetch("/api/login", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ Password: passwordInput.value })
        });
        
        if (response.ok) {
            window.location.href = "/index.html";
        } else {
            const data = await response.json();
            errorDiv.textContent = data.error || "Authentication failed.";
            errorDiv.style.display = "block";
        }
    } catch (e) {
        errorDiv.textContent = "Error communicating with server.";
        errorDiv.style.display = "block";
    }
}
