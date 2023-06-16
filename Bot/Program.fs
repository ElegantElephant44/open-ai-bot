open System
open System.Net
open System.Runtime.Caching
open Funogram.Api
open Funogram.Telegram
open Funogram.Telegram.Bot
open System.Net.Http
open System.Net.Http.Json
open System.Net.Http.Headers
open Funogram.Telegram.Types
open Funogram.Types
open Microsoft.FSharp.Core
open Microsoft.Extensions.Logging

type Message = { role: string; content: string }

type Choice = { message: Message }
type Response = { id: string; choices: List<Choice> }

type ImageData = { url: string }

type ImageResponse = { data: ImageData[] }

type ResponseError =
    | General of string
    | TooManyTokens

let createLogger name =
    use loggerFactory =
        LoggerFactory.Create(fun b ->
            b.AddSimpleConsole(fun c -> c.TimestampFormat <- "[yyyy-MM-dd HH:mm:ss] ")
            |> ignore)

    loggerFactory.CreateLogger(name)


let createHttpClient authToken =
    let client = new HttpClient()
    client.BaseAddress <- Uri("https://api.openai.com/v1/")
    client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", authToken)
    client

let sendChatRequest (client: HttpClient) messages =
    task {
        let partialPost =
            {| model = "gpt-3.5-turbo"
               messages = messages |}

        let! response = client.PostAsJsonAsync("chat/completions", partialPost)

        match response.StatusCode with
        | HttpStatusCode.OK ->
            let! responseDeserialized = response.Content.ReadFromJsonAsync<Response>()
            return Ok(responseDeserialized.choices |> List.map (fun c -> c.message))
        | _ ->
            let! responseText = response.Content.ReadAsStringAsync()
            if (responseText.Contains("context_length_exceeded"))
            then return Error (TooManyTokens)
            else return Error (General($"Response wasn't successful. Response {responseText}. StatusCode {response.StatusCode}"))
    }

let rec sendToChatRecursive (client: HttpClient) messages depth =
    let maxDepth = 10
    task {
        let! response = sendChatRequest client messages
        match response with
        | Ok responseMessages -> return Ok (messages @ responseMessages, responseMessages)
        | Error errorValue ->
            match errorValue with
            | TooManyTokens when depth < maxDepth -> 
                let! result = sendToChatRecursive client (List.tail messages) (depth + 1)
                return result
            | TooManyTokens -> return Ok ([], [{role = "system"; content = "Too many tokens, new converstaion was started"}])
            | General m -> return Error(m)            
    }

let sendImageRequest (client: HttpClient) (prompt: string) =
    task {
        let partialPost =
            {| prompt = prompt
               size = "512x512"
               n = 1 |}

        let! response =
            let url = "images/generations"
            client.PostAsJsonAsync(url, partialPost)

        let! responseDeserialized = response.Content.ReadFromJsonAsync<ImageResponse>()
        return responseDeserialized.data |> Array.map (fun c -> c.url)
    }

let getMessages (previousMessages: List<Message>) originalText =
    let contextLimit = 20
    let messages = if previousMessages.Length <= contextLimit then previousMessages else previousMessages[previousMessages.Length - contextLimit..]
    match (originalText, messages) with
    | text, pMessages when text = "/regenerate" && pMessages.Length <= 1 -> []
    | text, pMessages when text = "/regenerate" -> pMessages[.. pMessages.Length - 2]
    | _ ->
        messages
        @ [ { role = "user"
              content = originalText } ]

let updateArrived (storage: MemoryCache) sendChatRequest sendImageRequest (logger: ILogger) (ctx: UpdateContext) =
    try
        match ctx.Update.Message with
        | Some { Text = Some "/newconversation"
                 From = Some from } ->
            let stringId = from.Id.ToString()
            storage.Remove(stringId) |> ignore
        | Some { Chat = chat
                 Text = Some originalText } when originalText.StartsWith "/image " ->
            async {
                let prompt = originalText.Replace("/image ", "")
                logger.LogInformation prompt
                let! urls = sendImageRequest prompt |> Async.AwaitTask

                return!
                    urls
                    |> Array.map (fun u ->
                        let input = InputFile.Url(Uri(u))
                        Api.sendPhoto chat.Id input 0 |> api ctx.Config)
                    |> Async.Sequential
            }
            |> Async.Ignore
            |> Async.Start
        | Some { MessageId = messageId
                 Chat = chat
                 Text = Some originalText
                 From = Some from } ->
            async {
                try
                    logger.LogInformation originalText
                    let stringId = from.Id.ToString()
                    let cached = storage.Get(stringId)

                    let previousMessages =
                        if (cached :? List<Message>) then
                            cached :?> List<Message>
                        else
                            List.empty<Message>

                    let messages = getMessages previousMessages originalText

                    if messages.IsEmpty then
                        return! Api.sendMessage chat.Id "Conversation is empty" |> api ctx.Config
                    else
                        let! response = sendChatRequest messages 0 |> Async.AwaitTask
                        match response with
                        | Ok (previous, responseMessages) -> 
                            storage.Set(stringId, previous, DateTimeOffset.Now.AddMinutes(60.0))

                            let responseText =
                                responseMessages |> List.map (fun m -> m.content) |> String.concat " "

                            logger.LogInformation responseText
                            return! Api.sendMessageReply chat.Id responseText messageId |> api ctx.Config
                        | Error errorValue ->
                            logger.LogError(errorValue)
                            return! Api.sendMessageReply chat.Id "Failed to get response from OpenAI" messageId |> api ctx.Config
                with ex ->
                    logger.LogError(ex, "Error during processing message.")

                    return
                        Result.Error
                            { Description = "Failed to process message"
                              ErrorCode = 500 }
            }
            |> Async.Ignore
            |> Async.Start
        | _ -> ()
    with ex ->
        logger.LogError(ex, "Error during processing message.")

[<EntryPoint>]
let main _ =
    let logger = createLogger "main"
    let openAiAPIKey = Environment.GetEnvironmentVariable("OPEN_AI_TOKEN")
    use storage = new MemoryCache("cache")
    use client = createHttpClient openAiAPIKey
    let sendChat =  sendToChatRecursive client
    let sendImage = sendImageRequest client
    let updateProcessor = updateArrived storage sendChat sendImage logger

    async {
        let config = Config.defaultConfig |> Config.withReadTokenFromEnv "TELEGRAM_TOKEN"
        let! _ = Api.deleteWebhookBase () |> api config
        return! startBot config updateProcessor None
    }
    |> Async.RunSynchronously

    0
